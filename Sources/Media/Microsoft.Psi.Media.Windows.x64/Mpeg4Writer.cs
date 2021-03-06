﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

namespace Microsoft.Psi.Media
{
    using System;
    using Microsoft.Psi;
    using Microsoft.Psi.Audio;
    using Microsoft.Psi.Components;
    using Microsoft.Psi.Imaging;
    using Microsoft.Psi.Media_Interop;

    /// <summary>
    /// This class defines a component for writing image+sound data into an MPEG-4 file (.mp4)
    /// </summary>
    public class Mpeg4Writer : IConsumer<Shared<Image>>, IStartable, IDisposable
    {
        private readonly Pipeline pipeline;
        private readonly Mpeg4WriterConfiguration configuration;
        private string filename;
        private MP4Writer writer;

        /// <summary>
        /// Initializes a new instance of the <see cref="Mpeg4Writer"/> class.
        /// </summary>
        /// <param name="pipeline">Pipeline this component is a part of</param>
        /// <param name="filename">Name of output file to write to</param>
        /// <param name="configurationFilename">Name of file containing media capture device configuration</param>
        public Mpeg4Writer(Pipeline pipeline, string filename, string configurationFilename)
            : this(pipeline, filename)
        {
            var configurationHelper = new ConfigurationHelper<Mpeg4WriterConfiguration>(configurationFilename);
            this.configuration = configurationHelper.Configuration;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="Mpeg4Writer"/> class.
        /// </summary>
        /// <param name="pipeline">Pipeline this component is a part of</param>
        /// <param name="filename">Name of output file to write to</param>
        /// <param name="configuration">Describes how to configure the media capture device</param>
        public Mpeg4Writer(Pipeline pipeline, string filename, Mpeg4WriterConfiguration configuration)
            : this(pipeline, filename)
        {
            this.configuration = configuration;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="Mpeg4Writer"/> class.
        /// </summary>
        /// <param name="pipeline">Pipeline this component is a part of</param>
        /// <param name="filename">Name of output file to write to</param>
        /// <param name="width">Width of output image in pixels</param>
        /// <param name="height">Height of output image in pixels</param>
        /// <param name="pixelFormat">Format of input images</param>
        public Mpeg4Writer(Pipeline pipeline, string filename, uint width, uint height, Imaging.PixelFormat pixelFormat)
            : this(pipeline, filename)
        {
            this.configuration = Mpeg4WriterConfiguration.Default;
            this.configuration.ImageWidth = width;
            this.configuration.ImageHeight = height;
            this.configuration.PixelFormat = pixelFormat;
        }

        private Mpeg4Writer(Pipeline pipeline, string filename)
        {
            this.pipeline = pipeline;
            this.ImageIn = pipeline.CreateReceiver<Shared<Image>>(this, this.ReceiveImage, nameof(this.ImageIn));
            this.AudioIn = pipeline.CreateReceiver<AudioBuffer>(this, this.ReceiveAudio, nameof(this.AudioIn));
            this.filename = filename;
        }

        /// <summary>
        /// Gets or sets the input stream of images
        /// </summary>
        public Receiver<Shared<Image>> ImageIn { get; set; }

        /// <summary>
        /// Gets or sets the input stream of images
        /// </summary>
        public Receiver<AudioBuffer> AudioIn { get; set; }

        /// <summary>
        /// Gets the input stream of images
        /// </summary>
        public Receiver<Shared<Image>> In => this.ImageIn;

        /// <summary>
        /// Called once all the subscriptions are established.
        /// </summary>
        /// <param name="onCompleted">Delegate to call when the execution completed</param>
        /// <param name="descriptor">If set, describes the playback constraints</param>
        void IStartable.Start(Action onCompleted, ReplayDescriptor descriptor)
        {
            MP4Writer.Startup();
            this.writer = new MP4Writer();
            this.writer.Open(this.filename, this.configuration.Config);
        }

        /// <summary>
        /// Called by the pipeline when media capture should be stopped
        /// </summary>
        void IStartable.Stop()
        {
            this.writer.Close();
            this.writer = null;
            MP4Writer.Shutdown();
        }

        /// <summary>
        /// Dispose method
        /// </summary>
        public void Dispose()
        {
            // check for null since it's possible that Start was never called
            if (this.writer != null)
            {
                this.writer.Close();
                this.writer.Dispose();
                this.writer = null;
                MP4Writer.Shutdown();
            }
        }

        private void ReceiveImage(Shared<Image> image, Envelope e)
        {
            if (this.writer != null)
            {
                this.writer.WriteVideoFrame(e.OriginatingTime.Ticks, image.Resource.ImageData, (uint)image.Resource.Width, (uint)image.Resource.Height, (int)image.Resource.PixelFormat);
            }
        }

        private void ReceiveAudio(AudioBuffer audioBuffer, Envelope env)
        {
            if (this.writer != null)
            {
                System.IntPtr waveFmtPtr = System.Runtime.InteropServices.Marshal.AllocHGlobal((int)WaveFormat.MarshalSizeOf(audioBuffer.Format) + sizeof(int));
                WaveFormat.MarshalToPtr(audioBuffer.Format, waveFmtPtr);
                System.IntPtr audioData = System.Runtime.InteropServices.Marshal.AllocHGlobal(audioBuffer.Length);
                System.Runtime.InteropServices.Marshal.Copy(audioBuffer.Data, 0, audioData, audioBuffer.Length);
                this.writer.WriteAudioSample(env.OriginatingTime.Ticks, audioData, (uint)audioBuffer.Length, waveFmtPtr);
                System.Runtime.InteropServices.Marshal.FreeHGlobal(waveFmtPtr);
                System.Runtime.InteropServices.Marshal.FreeHGlobal(audioData);
            }
        }
    }
}
