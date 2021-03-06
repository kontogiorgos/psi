﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

namespace Microsoft.Psi.Audio
{
    using System;
    using System.Runtime.InteropServices;
    using Microsoft.Psi;
    using Microsoft.Psi.Components;

    /// <summary>
    /// Component that implements an audio source which captures live audio from an input device such as a microphone.
    /// </summary>
    /// <remarks>
    /// This sensor component produces an audio output stream of type <see cref="AudioBuffer"/> which may be piped to
    /// downstream components for further processing and optionally saved to a data store. The audio input device from
    /// which to capture may be specified via the <see cref="AudioSourceConfiguration.DeviceName"/> configuration
    /// parameter. The <see cref="GetAvailableDevices"/> static method may be used to enumerate the names of audio
    /// input devices currently available on the system.
    /// <br/>
    /// **Please note**: This component uses Audio APIs that are available on Windows only.
    /// </remarks>
    public sealed class AudioSource : IProducer<AudioBuffer>, IStartable, IDisposable
    {
        private readonly Pipeline pipeline;

        /// <summary>
        /// The configuration for this component.
        /// </summary>
        private readonly AudioSourceConfiguration configuration;

        /// <summary>
        /// The output stream of audio buffers.
        /// </summary>
        private readonly Emitter<AudioBuffer> audioBuffers;

        /// <summary>
        /// The audio capture device
        /// </summary>
        private AudioCapture audioCaptureDevice;

        /// <summary>
        /// The current audio capture buffer.
        /// </summary>
        private AudioBuffer buffer;

        /// <summary>
        /// The current source audio format.
        /// </summary>
        private WaveFormat sourceFormat = null;

        /// <summary>
        /// Keep track of the timestamp of the last audio buffer (computed from the value reported to us by the capture driver).
        /// </summary>
        private DateTime lastPostedAudioTime = DateTime.MinValue;

        /// <summary>
        /// Initializes a new instance of the <see cref="AudioSource"/> class.
        /// </summary>
        /// <param name="pipeline">The pipeline to add the component to.</param>
        /// <param name="configuration">The component configuration.</param>
        public AudioSource(Pipeline pipeline, AudioSourceConfiguration configuration)
        {
            this.pipeline = pipeline;
            this.configuration = configuration;
            this.audioBuffers = pipeline.CreateEmitter<AudioBuffer>(this, "AudioBuffers");
            this.AudioLevelInput = pipeline.CreateReceiver<double>(this, this.SetAudioLevel, nameof(this.AudioLevelInput), true);
            this.AudioLevel = pipeline.CreateEmitter<double>(this, nameof(this.AudioLevel));

            this.audioCaptureDevice = new AudioCapture();
            this.audioCaptureDevice.Initialize(this.Configuration.DeviceName);

            if (this.Configuration.AudioLevel >= 0)
            {
                this.audioCaptureDevice.AudioLevel = this.Configuration.AudioLevel;
            }
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="AudioSource"/> class.
        /// </summary>
        /// <param name="pipeline">The pipeline to add the component to.</param>
        /// <param name="configurationFilename">The component configuration file.</param>
        public AudioSource(Pipeline pipeline, string configurationFilename = null)
            : this(
                pipeline,
                (configurationFilename == null) ? new AudioSourceConfiguration() : new ConfigurationHelper<AudioSourceConfiguration>(configurationFilename).Configuration)
        {
        }

        /// <summary>
        /// Gets the output stream of audio buffers.
        /// </summary>
        public Emitter<AudioBuffer> Out
        {
            get { return this.audioBuffers; }
        }

        /// <summary>
        /// Gets the level control input.
        /// </summary>
        public Receiver<double> AudioLevelInput { get; }

        /// <summary>
        /// Gets the output stream of audio level data.
        /// </summary>
        public Emitter<double> AudioLevel { get; }

        /// <summary>
        /// Gets the name of the audio device.
        /// </summary>
        public string AudioDeviceName
        {
            get { return this.audioCaptureDevice.Name; }
        }

        /// <summary>
        /// Gets the configuration for this component.
        /// </summary>
        private AudioSourceConfiguration Configuration
        {
            get { return this.configuration; }
        }

        /// <summary>
        /// Static method to get the available audio capture devices.
        /// </summary>
        /// <returns>
        /// An array of available capture device names.
        /// </returns>
        public static string[] GetAvailableDevices()
        {
            return AudioCapture.GetAvailableCaptureDevices();
        }

        /// <summary>
        /// Sets the audio level.
        /// </summary>
        /// <param name="level">The audio level.</param>
        public void SetAudioLevel(Message<double> level)
        {
            if (this.audioCaptureDevice != null)
            {
                this.audioCaptureDevice.AudioLevel = level.Data;
            }
        }

        /// <summary>
        /// Called to start capturing audio from the microphone.
        /// </summary>
        /// <param name="onCompleted">Delegate to call when the execution completed</param>
        /// <param name="descriptor">If set, describes the playback constraints</param>
        void IStartable.Start(Action onCompleted, ReplayDescriptor descriptor)
        {
            // publish initial values at startup
            this.AudioLevel.Post(this.audioCaptureDevice.AudioLevel, this.pipeline.GetCurrentTime());

            // register the event handler which will post new captured samples on the output stream
            this.audioCaptureDevice.AudioDataAvailableEvent += this.HandleAudioDataAvailableEvent;

            // register the volume notification event handler
            this.audioCaptureDevice.AudioVolumeNotification += this.HandleAudioVolumeNotification;

            // tell the audio device to start capturing audio
            this.audioCaptureDevice.StartCapture(this.Configuration.TargetLatencyInMs, this.Configuration.Gain, this.Configuration.OutputFormat, this.Configuration.OptimizeForSpeech);

            // Get the actual capture format. This should normally match the configured output format,
            // unless that was null in which case the native device capture format is returned.
            this.sourceFormat = this.Configuration.OutputFormat ?? this.audioCaptureDevice.MixFormat;
        }

        /// <summary>
        /// Called when the pipeline is shutting down.
        /// </summary>
        void IStartable.Stop()
        {
            this.sourceFormat = null;
        }

        /// <summary>
        /// Dispose method.
        /// </summary>
        public void Dispose()
        {
            if (this.audioCaptureDevice != null)
            {
                this.audioCaptureDevice.Dispose();
                this.audioCaptureDevice = null;
            }
        }

        /// <summary>
        /// The event handler that processes new audio data packets.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">A <see cref="AudioDataEventArgs"/> that contains the event data.</param>
        private void HandleAudioDataAvailableEvent(object sender, AudioDataEventArgs e)
        {
            if ((e.Length > 0) && (this.sourceFormat != null))
            {
                // use the end of the last sample in the packet as the originating time
                DateTime originatingTime = this.pipeline.GetCurrentTimeFromElapsedTicks(e.Timestamp +
                    (10000000L * e.Length / this.sourceFormat.AvgBytesPerSec));

                // Detect out of order originating times
                if (originatingTime < this.lastPostedAudioTime)
                {
                    if (this.configuration.DropOutOfOrderPackets)
                    {
                        // Ignore this packet with an out of order timestamp and return.
                        return;
                    }
                    else
                    {
                        throw new InvalidOperationException(string.Format(
                            "The most recently captured audio buffer has a timestamp ({0}) which is before " +
                            "that of the last posted audio buffer ({1}), as reported by the driver. This could " +
                            "be due to a timing glitch in the audio stream. Set the 'DropOutOfOrderPackets' " +
                            "AudioSourceConfiguration flag to true to handle this condition by dropping " +
                            "packets with out of order timestamps.",
                            originatingTime.TimeOfDay,
                            this.lastPostedAudioTime.TimeOfDay));
                    }
                }

                this.lastPostedAudioTime = originatingTime;

                // Only create a new buffer if necessary.
                if ((this.buffer.Data == null) || (this.buffer.Length != e.Length))
                {
                    this.buffer = new AudioBuffer(e.Length, this.sourceFormat);
                }

                // Copy the data.
                Marshal.Copy(e.Data, this.buffer.Data, 0, e.Length);

                // post the data to the output stream
                this.audioBuffers.Post(this.buffer, originatingTime);
            }
        }

        /// <summary>
        /// Handles volume notifications
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">A <see cref="AudioVolumeEventArgs"/> that contains the event data.</param>
        private void HandleAudioVolumeNotification(object sender, AudioVolumeEventArgs e)
        {
            this.AudioLevel.Post(e.MasterVolume, this.pipeline.GetCurrentTime());
        }
    }
}
