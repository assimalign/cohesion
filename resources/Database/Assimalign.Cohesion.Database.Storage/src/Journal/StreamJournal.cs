using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace Assimalign.Cohesion.Database.Storage;

using Assimalign.Cohesion.Database.Storage.Internal;

/// <summary>
/// Stream-backed write-ahead log implementation. Frames append sequentially at the
/// end of the stream; durable flushes use <see cref="FileStream.Flush(bool)"/> when
/// the underlying stream is a file.
/// </summary>
public sealed class StreamJournal : Journal
{
    private readonly Stream _stream;
    private readonly bool _leaveOpen;

    /// <summary>
    /// Initializes a stream-backed journal.
    /// </summary>
    /// <param name="stream">Readable, writable, seekable stream.</param>
    /// <param name="leaveOpen">When true, the stream is not disposed with the journal.</param>
    /// <exception cref="ArgumentException">The stream does not support read, write, and seek.</exception>
    public StreamJournal(Stream stream, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(stream);

        if (!stream.CanRead || !stream.CanWrite || !stream.CanSeek)
        {
            throw new ArgumentException("Journal stream must support read, write, and seek.", nameof(stream));
        }

        _stream = stream;
        _leaveOpen = leaveOpen;
    }

    /// <summary>
    /// Creates a file-backed journal.
    /// </summary>
    /// <param name="path">Journal file path.</param>
    /// <returns>Created journal instance.</returns>
    public static StreamJournal FromFile(string path)
    {
        var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        return new StreamJournal(stream);
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
        if (forceDurable && _stream is FileStream fileStream)
        {
            fileStream.Flush(flushToDisk: true);
            return;
        }

        _stream.Flush();
    }

    /// <inheritdoc />
    protected override IEnumerable<ReadOnlyMemory<byte>> ReadFrames()
    {
        var frames = new List<ReadOnlyMemory<byte>>();
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

                frames.Add(body);
            }

            return frames;
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
