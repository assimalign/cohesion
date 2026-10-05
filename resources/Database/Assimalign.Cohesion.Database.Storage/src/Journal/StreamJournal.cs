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

    // Where the next frames go: the end of the last frame that verifies, once a read scan has
    // run to the end of the verified frames or a write has landed; -1 until then. Bytes past
    // it are a torn tail, which the next write cuts off first.
    private long _appendOffset = -1;

    // Whether bytes may follow _appendOffset: set by a completed read scan, cleared once a
    // write has cut them off. While clear, the stream ends at _appendOffset (this journal is
    // its only writer), so a write needs no length query.
    private bool _tailUnchecked;

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
    /// <remarks>
    /// <para>
    /// The frames go to the stream in one positional write at the end of the last frame
    /// written, so a drain costs one system call however many records it carries (#1252).
    /// </para>
    /// <para>
    /// A frame never lands after a torn tail a crash left: the first write after a reopen goes
    /// to the end of the last frame that verified when the journal was read, cutting off
    /// whatever follows it, rather than to the end of the stream. Otherwise every record
    /// appended before the next truncation — an engine's recovery scrub runs before its
    /// open-time checkpoint — would sit behind bytes the next read scan stops at, and a second
    /// crash would lose them. PostgreSQL likewise resumes WAL insertion at the end of the last
    /// valid record (<c>EndOfLog</c>, <c>src/backend/access/transam/xlog.c:6711-6718</c>).
    /// </para>
    /// <para>
    /// A write that fails part way can leave the start of a frame at the end of the stream.
    /// The failure takes the journal offline, so nothing is written behind those bytes; the
    /// reopen's read scan stops at them and its first write cuts them off. PostgreSQL stops on
    /// any failed WAL write for the same reason (<c>ereport(PANIC, "could not write to log file
    /// ...")</c>, <c>src/backend/access/transam/xlog.c:2529-2532</c>). Until #1252 the journal cut
    /// a partial frame back off and kept appending, which a buffered journal cannot do safely:
    /// the frames a failed drain carried are already described by pages in the buffer pool.
    /// </para>
    /// </remarks>
    protected override void WriteFramesCore(ReadOnlySpan<byte> frames)
    {
        long start = _appendOffset >= 0 ? _appendOffset : _handle.Length;

        if (_tailUnchecked)
        {
            if (_handle.Length > start)
            {
                // A torn tail: no frame after it would ever be read.
                _handle.SetLength(start);
            }

            _tailUnchecked = false;
        }

        _handle.Write(frames, start);
        _appendOffset = start + frames.Length;
    }

    /// <inheritdoc />
    protected override void FlushCore(bool forceDurable)
    {
        _handle.Flush(durable: forceDurable);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The scan reads the medium in <see cref="ReadChunkSize"/> chunks, one positional read each,
    /// and cuts the frames out of the chunk; a frame larger than a chunk is read whole. It used to
    /// make three reads per frame (prefix, body, checksum), which cost nothing beside two 8 KiB
    /// page images per touched page but dominated recovery once a commit journals a few dozen
    /// bytes per page (#1253): about 470,000 small frames took 37 s to reopen from a file, three
    /// passes of three system calls each.
    /// </remarks>
    protected override IEnumerable<ReadOnlyMemory<byte>> ReadFrames()
    {
        long originalPosition = _stream.Position;

        try
        {
            long length = _handle.Length;
            var chunk = new byte[(int)Math.Min(ReadChunkSize, Math.Max(length, FramePrefixSize))];
            long chunkOffset = 0;
            int chunkCount = 0;
            long position = 0;

            while (position + FramePrefixSize <= length)
            {
                if (!Fill(ref chunk, ref chunkOffset, ref chunkCount, position, FramePrefixSize, length))
                {
                    break;
                }

                int start = (int)(position - chunkOffset);
                int bodyLength = BinaryPrimitives.ReadInt32LittleEndian(chunk.AsSpan(start));
                int magic = BinaryPrimitives.ReadInt32LittleEndian(chunk.AsSpan(start + sizeof(int)));
                if (magic != Magic || bodyLength < BodyHeaderSize || position + FramePrefixSize + bodyLength + sizeof(uint) > length)
                {
                    break;
                }

                int frameLength = FramePrefixSize + bodyLength + sizeof(uint);
                if (!Fill(ref chunk, ref chunkOffset, ref chunkCount, position, frameLength, length))
                {
                    break;
                }

                start = (int)(position - chunkOffset);
                var body = chunk.AsSpan(start + FramePrefixSize, bodyLength).ToArray();
                uint expected = BinaryPrimitives.ReadUInt32LittleEndian(chunk.AsSpan(start + FramePrefixSize + bodyLength));
                if (Crc32C.Compute(body) != expected)
                {
                    break;
                }

                position += frameLength;
                yield return body;
            }

            // Reached only when the scan ran to the end of the verified frames, never when the
            // caller stopped enumerating early: the next append goes here.
            _appendOffset = position;
            _tailUnchecked = true;
        }
        finally
        {
            _stream.Seek(originalPosition, SeekOrigin.Begin);
        }
    }

    /// <summary>
    /// The size of the chunks a read scan reads the medium in.
    /// </summary>
    private const int ReadChunkSize = 256 * 1024;

    /// <summary>
    /// Makes the <paramref name="count"/> bytes at <paramref name="position"/> available in the
    /// chunk, reading the medium from <paramref name="position"/> when they are not (growing the
    /// chunk for a frame larger than it).
    /// </summary>
    /// <returns>False when the medium ends before the bytes do.</returns>
    private bool Fill(ref byte[] chunk, ref long chunkOffset, ref int chunkCount, long position, int count, long length)
    {
        if (position >= chunkOffset && position + count <= chunkOffset + chunkCount)
        {
            return true;
        }

        if (position + count > length)
        {
            return false;
        }

        if (count > chunk.Length)
        {
            chunk = new byte[count];
        }

        int wanted = (int)Math.Min(chunk.Length, length - position);
        int total = 0;
        while (total < wanted)
        {
            int read = _handle.Read(chunk.AsSpan(total, wanted - total), position + total);
            if (read <= 0)
            {
                break;
            }

            total += read;
        }

        chunkOffset = position;
        chunkCount = total;
        return total >= count;
    }

    /// <inheritdoc />
    private protected override void ReadAtCore(long offset, Span<byte> destination)
    {
        int total = 0;
        while (total < destination.Length)
        {
            int read = _handle.Read(destination[total..], offset + total);
            if (read <= 0)
            {
                throw new StorageCorruptionException(
                    $"The journal holds {_handle.Length} bytes; reading {destination.Length} bytes at offset {offset} passed its end.");
            }

            total += read;
        }
    }

    /// <inheritdoc />
    protected override void TruncateCore()
    {
        _stream.SetLength(0);
        _appendOffset = 0;
        _tailUnchecked = false;
    }

    /// <inheritdoc />
    protected override void DisposeCore()
    {
        if (!_leaveOpen)
        {
            _stream.Dispose();
        }
    }
}
