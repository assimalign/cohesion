using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace Assimalign.Cohesion.Database.Storage;

using Assimalign.Cohesion.Database.Storage.Internal;
using Assimalign.Cohesion.FileSystem;

/// <summary>
/// Stream-backed write-ahead log implementation. Frames append sequentially at the
/// end of the stream; durable flushes are carried by an explicit
/// <see cref="IFileSystemFileHandle"/> contract.
/// </summary>
public sealed class StreamJournal : StorageJournal
{
    private readonly Stream _stream;
    private readonly IFileSystemFileHandle _handle;
    private readonly bool _leaveOpen;

    /// <summary>
    /// Initializes a non-durable stream-backed journal. Use a handle or
    /// <see cref="StorageStream"/> to supply an explicit durability contract.
    /// </summary>
    /// <param name="stream">Readable, writable, seekable stream.</param>
    /// <param name="leaveOpen">When true, the stream is not disposed with the journal.</param>
    /// <exception cref="ArgumentException">The stream does not support read, write, and seek.</exception>
    public StreamJournal(Stream stream, bool leaveOpen = false)
        : this(stream, new StorageStream(stream), leaveOpen)
    {
    }

    /// <summary>Initializes a journal that retains the storage stream's durability contract.</summary>
    /// <param name="stream">Readable, writable, seekable storage stream.</param>
    /// <param name="leaveOpen">When true, the stream is not disposed with the journal.</param>
    public StreamJournal(StorageStream stream, bool leaveOpen = false)
        : this(stream, stream, leaveOpen)
    {
    }

    /// <summary>Initializes a journal backed by an explicit random-access file handle.</summary>
    /// <param name="handle">The file handle providing I/O and durability.</param>
    /// <param name="leaveOpen">When true, the handle is not disposed with the journal.</param>
    public StreamJournal(IFileSystemFileHandle handle, bool leaveOpen = false)
        : this(new StorageStream(handle), leaveOpen)
    {
    }

    private StreamJournal(Stream stream, IFileSystemFileHandle handle, bool leaveOpen)
    {
        ArgumentNullException.ThrowIfNull(stream);

        if (!stream.CanRead || !stream.CanWrite || !stream.CanSeek)
        {
            throw new ArgumentException("Journal stream must support read, write, and seek.", nameof(stream));
        }

        _stream = stream;
        _handle = handle;
        _leaveOpen = leaveOpen;
    }

    /// <summary>
    /// Creates a file-backed journal.
    /// </summary>
    /// <param name="path">Journal file path.</param>
    /// <returns>Created journal instance.</returns>
    public static StreamJournal FromFile(string path)
    {
        return new StreamJournal(StorageFileSystem.OpenHandle(path, null, FileShare.Read));
    }

    /// <summary>Creates a journal through the supplied file system.</summary>
    /// <param name="path">Journal path, relative to the supplied file system.</param>
    /// <param name="fileSystem">The file system used to open the journal.</param>
    /// <returns>Created journal instance.</returns>
    /// <remarks>A non-durable provider is rejected at the first requested durable flush.</remarks>
    public static StreamJournal FromFile(string path, IFileSystem fileSystem)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        return new StreamJournal(StorageFileSystem.OpenHandle(path, fileSystem, FileShare.Read));
    }

    /// <inheritdoc />
    protected override void AppendFrame(ReadOnlySpan<byte> frame)
    {
        _stream.Seek(0, SeekOrigin.End);
        _stream.Write(frame);
    }

    /// <inheritdoc />
    protected override void FlushCore(bool forceDurable)
    {
        _handle.Flush(durable: forceDurable);
    }

    /// <inheritdoc />
    protected override IEnumerable<ReadOnlyMemory<byte>> ReadFrames()
    {
        long originalPosition = _stream.Position;

        try
        {
            _stream.Seek(0, SeekOrigin.Begin);
            var prefix = new byte[FramePrefixSize];

            while (_stream.Position + FramePrefixSize <= _stream.Length)
            {
                if (!ReadExactly(prefix))
                {
                    break;
                }

                int bodyLength = BinaryPrimitives.ReadInt32LittleEndian(prefix);
                int magic = BinaryPrimitives.ReadInt32LittleEndian(prefix.AsSpan(4));

                if (magic != Magic || bodyLength < BodyHeaderSize ||
                    _stream.Position + bodyLength + sizeof(uint) > _stream.Length)
                {
                    break;
                }

                var body = new byte[bodyLength];
                var checksumBuffer = new byte[sizeof(uint)];

                if (!ReadExactly(body) || !ReadExactly(checksumBuffer))
                {
                    break;
                }

                uint expected = BinaryPrimitives.ReadUInt32LittleEndian(checksumBuffer);
                if (Crc32.Compute(body) != expected)
                {
                    break;
                }

                yield return body;
            }
        }
        finally
        {
            _stream.Seek(originalPosition, SeekOrigin.Begin);
        }
    }

    /// <inheritdoc />
    protected override void TruncateCore()
    {
        _stream.SetLength(0);
    }

    /// <inheritdoc />
    protected override void DisposeCore()
    {
        if (!_leaveOpen)
        {
            _stream.Dispose();
        }
    }

    private bool ReadExactly(byte[] buffer)
    {
        int totalRead = 0;
        while (totalRead < buffer.Length)
        {
            int bytesRead = _stream.Read(buffer, totalRead, buffer.Length - totalRead);
            if (bytesRead == 0)
            {
                return false;
            }
            totalRead += bytesRead;
        }

        return true;
    }
}
