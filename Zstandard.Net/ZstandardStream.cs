﻿using System;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using Interop = Zstandard.Net.ZstandardInterop;

namespace Zstandard.Net
{
    /// <summary>
    /// Provides methods and properties for compressing and decompressing streams by using the Zstandard algorithm.
    /// </summary>
    public class ZstandardStream : Stream
    {
        private Stream stream;
        private CompressionMode mode;
        private Boolean leaveOpen;
        private Boolean isClosed = false;
        private Boolean isInitialized = false;

        private IntPtr zstream;
        private uint zstreamInputSize;
        private uint zstreamOutputSize;

        private byte[] data;
        private bool dataDepleted = false;
        private int dataPosition = 0;
        private int dataSize = 0;

        private Interop.Buffer outputBuffer = new Interop.Buffer();
        private Interop.Buffer inputBuffer  = new Interop.Buffer();

        /// <summary>
        /// Initializes a new instance of the <see cref="ZstandardStream"/> class by using the specified stream and compression mode, and optionally leaves the stream open.
        /// </summary>
        /// <param name="stream">The stream to compress.</param>
        /// <param name="mode">One of the enumeration values that indicates whether to compress or decompress the stream.</param>
        /// <param name="leaveOpen">true to leave the stream open after disposing the <see cref="ZstandardStream"/> object; otherwise, false.</param>
        public ZstandardStream(Stream stream, CompressionMode mode, bool leaveOpen = false)
        {
            this.stream = stream ?? throw new ArgumentNullException(nameof(stream));
            this.mode = mode;
            this.leaveOpen = leaveOpen;

            if (mode == CompressionMode.Compress)
            {
                this.zstreamInputSize = Interop.ZSTD_CStreamInSize().ToUInt32();
                this.zstreamOutputSize = Interop.ZSTD_CStreamOutSize().ToUInt32();
                this.zstream = Interop.ZSTD_createCStream();
                this.data = new byte[this.zstreamOutputSize];
            }

            if (mode == CompressionMode.Decompress)
            {
                this.zstreamInputSize = Interop.ZSTD_DStreamInSize().ToUInt32();
                this.zstreamOutputSize = Interop.ZSTD_DStreamOutSize().ToUInt32();
                this.zstream = Interop.ZSTD_createDStream();
                this.data = new byte[this.zstreamInputSize];
            }
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="ZstandardStream"/> class  by using the specified stream and compression level, and optionally leaves the stream open.
        /// </summary>
        /// <param name="stream">The stream to compress.</param>
        /// <param name="compressionLevel">The compression level.</param>
        /// <param name="leaveOpen">true to leave the stream open after disposing the <see cref="ZstandardStream"/> object; otherwise, false.</param>
        public ZstandardStream(Stream stream, int compressionLevel, bool leaveOpen = false) : this(stream, CompressionMode.Compress, leaveOpen)
        {
            this.CompressionLevel = compressionLevel;
        }

        //-----------------------------------------------------------------------------------------
        //-----------------------------------------------------------------------------------------

        /// <summary>
        /// The version of the native Zstd library.
        /// </summary>
        public static Version Version
        {
            get
            {
                var version = (int)Interop.ZSTD_versionNumber();
                return new Version((version / 10000) % 100, (version / 100) % 100, version % 100);
            }
        }

        /// <summary>
        /// The maximum compression level supported by the native Zstd library.
        /// </summary>
        public static int MaxCompressionLevel
        {
            get
            {
                return Interop.ZSTD_maxCLevel();
            }
        }

        //-----------------------------------------------------------------------------------------
        //-----------------------------------------------------------------------------------------

        /// <summary>
        /// Gets or sets the compression level to use, the default is 6.
        /// </summary>
        /// <remarks>
        /// To get the maximum compression level see <see cref="MaxCompressionLevel"/>.
        /// </remarks>
        public int CompressionLevel { get; set; } = 6;

        /// <summary>
        /// Gets whether the current stream supports reading.
        /// </summary>
        public override bool CanRead => this.stream.CanRead && this.mode == CompressionMode.Decompress;

        /// <summary>
        ///  Gets whether the current stream supports writing.
        /// </summary>
        public override bool CanWrite => this.stream.CanWrite && this.mode == CompressionMode.Compress;

        /// <summary>
        ///  Gets whether the current stream supports seeking.
        /// </summary>
        public override bool CanSeek => false;

        /// <summary>
        /// Gets the length in bytes of the stream.
        /// </summary>
        public override long Length => throw new NotSupportedException();

        /// <summary>
        /// Gets or sets the position within the current stream.
        /// </summary>
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        //-----------------------------------------------------------------------------------------
        //-----------------------------------------------------------------------------------------

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
        }

        public override void Close()
        {
            if (this.isClosed)
            {
                // do nothing
            }
            else if (this.mode == CompressionMode.Compress)
            {
                this.ProcessStream((zcs, buffer) => Interop.ThrowIfError(Interop.ZSTD_flushStream(zcs, buffer)));
                this.ProcessStream((zcs, buffer) => Interop.ThrowIfError(Interop.ZSTD_endStream(zcs, buffer)));
                this.stream.Flush();

                Interop.ZSTD_freeCStream(this.zstream);
                if (!this.leaveOpen) this.stream.Close();
            }
            else if (this.mode == CompressionMode.Decompress)
            {
                Interop.ZSTD_freeDStream(this.zstream);
                if (!this.leaveOpen) this.stream.Close();
            }

            this.isClosed = true;
            base.Close();
        }

        public override void Flush()
        {
            if (this.mode == CompressionMode.Compress)
            {
                this.ProcessStream((zcs, buffer) => Interop.ThrowIfError(Interop.ZSTD_flushStream(zcs, buffer)));
                this.stream.Flush();
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (this.CanRead == false) throw new NotSupportedException();
            if (count == 0) return 0;

            // prevent the buffers from being moved around by the garbage collector
            var alloc1 = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            var alloc2 = GCHandle.Alloc(this.data, GCHandleType.Pinned);

            try
            {
                var length = 0;

                if (this.isInitialized == false)
                {
                    this.isInitialized = true;
                    Interop.ZSTD_initDStream(this.zstream);
                }

                while (count > 0)
                {
                    var inputSize = this.dataSize - this.dataPosition;

                    // read data from input stream 
                    if (inputSize <= 0 && this.dataDepleted == false)
                    {
                        this.dataSize = this.stream.Read(this.data, 0, this.data.Length);
                        this.dataDepleted = this.dataSize <= 0;
                        this.dataPosition = 0;
                        inputSize = this.dataDepleted ? 0 : this.dataSize;
                    }

                    // configure the inputBuffer
                    this.inputBuffer.Data = this.dataDepleted ? IntPtr.Zero : Marshal.UnsafeAddrOfPinnedArrayElement(this.data, this.dataPosition);
                    this.inputBuffer.Size = this.dataDepleted ? UIntPtr.Zero : new UIntPtr((uint)inputSize);
                    this.inputBuffer.Position = UIntPtr.Zero;

                    // configure the outputBuffer
                    this.outputBuffer.Data = Marshal.UnsafeAddrOfPinnedArrayElement(buffer, offset);
                    this.outputBuffer.Size = new UIntPtr((uint)count);
                    this.outputBuffer.Position = UIntPtr.Zero;

                    // decompress inputBuffer to outputBuffer
                    Interop.ThrowIfError(Interop.ZSTD_decompressStream(this.zstream, this.outputBuffer, this.inputBuffer));

                    // calculate progress in outputBuffer
                    var outputBufferPosition = (int)this.outputBuffer.Position.ToUInt32();
                    if (outputBufferPosition == 0 && this.dataDepleted) break;
                    length += outputBufferPosition;
                    offset += outputBufferPosition;
                    count -= outputBufferPosition;

                    // calculate progress in inputBuffer
                    var inputBufferPosition = (int)inputBuffer.Position.ToUInt32();
                    this.dataPosition += inputBufferPosition;
                }

                return length;
            }
            finally
            {
                alloc1.Free();
                alloc2.Free();
            }
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (this.CanWrite == false) throw new NotSupportedException();

            // prevent the buffers from being moved around by the garbage collector
            var alloc1 = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            var alloc2 = GCHandle.Alloc(this.data, GCHandleType.Pinned);

            try
            {
                if (this.isInitialized == false)
                {
                    this.isInitialized = true;
                    Interop.ZSTD_initCStream(this.zstream, this.CompressionLevel);
                }

                while (count > 0)
                {
                    var inputSize = Math.Min((uint)count, this.zstreamInputSize);

                    // configure the outputBuffer
                    this.outputBuffer.Data = Marshal.UnsafeAddrOfPinnedArrayElement(this.data, 0);
                    this.outputBuffer.Size = new UIntPtr((uint)this.data.Length);
                    this.outputBuffer.Position = UIntPtr.Zero;

                    // configure the inputBuffer
                    this.inputBuffer.Data = Marshal.UnsafeAddrOfPinnedArrayElement(buffer, offset);
                    this.inputBuffer.Size = new UIntPtr((uint)inputSize);
                    this.inputBuffer.Position = UIntPtr.Zero;

                    // compress inputBuffer to outputBuffer
                    Interop.ThrowIfError(Interop.ZSTD_compressStream(this.zstream, this.outputBuffer, this.inputBuffer));

                    // write data to output stream
                    var outputBufferPosition = (int)this.outputBuffer.Position.ToUInt32();
                    this.stream.Write(this.data, 0, outputBufferPosition);

                    // calculate progress in inputBuffer
                    var inputBufferPosition = (int)this.inputBuffer.Position.ToUInt32();
                    offset += inputBufferPosition;
                    count -= inputBufferPosition;
                }
            }
            finally
            {
                alloc1.Free();
                alloc2.Free();
            }
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotImplementedException();
        }

        public override void SetLength(long value)
        {
            throw new NotImplementedException();
        }

        //-----------------------------------------------------------------------------------------
        //-----------------------------------------------------------------------------------------

        private void ProcessStream(Action<IntPtr, Interop.Buffer> outputAction)
        {
            var alloc = GCHandle.Alloc(this.data, GCHandleType.Pinned);

            try
            {
                this.outputBuffer.Data = Marshal.UnsafeAddrOfPinnedArrayElement(this.data, 0);
                this.outputBuffer.Size = new UIntPtr((uint)this.data.Length);
                this.outputBuffer.Position = UIntPtr.Zero;

                outputAction(this.zstream, this.outputBuffer);

                var outputBufferPosition = (int)this.outputBuffer.Position.ToUInt32();
                this.stream.Write(this.data, 0, outputBufferPosition);
            }
            finally
            {
                alloc.Free();
            }
        }
    }
}