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

    // Set when a failed append may have left part of its frame at the end of the stream
    // and cutting it back off failed too. Appends are refused until a truncation removes it.
    private bool _faulted;

    // Where the next frame goes: the end of the last frame that verifies, once a read scan has
    // run to the end of the verified frames or an append has landed; -1 until then. Bytes past
    // it are a torn tail, which the next append cuts off first.
    private long _appendOffset = -1;

    // Whether bytes may follow _appendOffset: set by a completed read scan, cleared once an
    // append has cut them off. While clear, the stream ends at _appendOffset (this journal is
    // its only writer), so an append needs no length query.
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
    /// A write that fails part way can leave the start of the frame at the end of the
    /// stream. Recovery's read scan stops at the first frame that does not verify, so a
    /// later frame appended after those bytes would be unreadable, and with it every commit
    /// record acknowledged after the failure. The failed append therefore cuts the stream
    /// back to where its frame began before the failure propagates. When that cut fails
    /// too, the journal refuses every later append with <see cref="JournalException"/>
    /// until a checkpoint's truncation removes the partial frame or the storage is
    /// reopened: a write that cannot reach recovery must fail rather than be acknowledged.
    /// PostgreSQL stops on any failed WAL write for the same reason (<c>ereport(PANIC,
    /// "could not write to log file ...")</c>, <c>src/backend/access/transam/xlog.c:2529-2531</c>,
    /// commit <c>85f55534e80</c>); this journal stops only its appends.
    /// <para>
    /// For the same reason a frame never lands after a torn tail a crash left: the first
    /// append after a reopen goes to the end of the last frame that verified when the journal
    /// was read, cutting off whatever follows it, rather than to the end of the stream.
    /// Otherwise every record appended before the next truncation — an engine's recovery scrub
    /// runs before its open-time checkpoint — would sit behind bytes the next read scan stops
    /// at, and a second crash would lose them. PostgreSQL likewise resumes WAL insertion at the
    /// end of the last valid record (<c>EndOfLog</c>, <c>src/backend/access/transam/xlog.c:6711-6718</c>).
    /// </para>
    /// </remarks>
    protected override void AppendFrame(ReadOnlySpan<byte> frame)
    {
        if (_faulted)
        {
            throw new JournalException(
                "The journal refuses appends: an earlier append failed part way and its partial frame could not be removed. " +
                "A checkpoint or a reopen of the storage clears the condition.");
        }

        long start = _appendOffset >= 0 ? _appendOffset : _stream.Seek(0, SeekOrigin.End);

        try
        {
            if (_tailUnchecked)
            {
                if (_stream.Length > start)
                {
                    // A torn tail: no frame after it would ever be read.
                    _stream.SetLength(start);
                }

                _tailUnchecked = false;
            }

            _stream.Seek(start, SeekOrigin.Begin);
            _stream.Write(frame);
        }
        catch
        {
            try
            {
                _stream.SetLength(start);
            }
            catch
            {
                _faulted = true;
            }

            throw;
        }

        _appendOffset = start + frame.Length;
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
            long verifiedEnd = 0;

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
                if (Crc32C.Compute(body) != expected)
                {
                    break;
                }

                verifiedEnd = _stream.Position;
                yield return body;
            }

            // Reached only when the scan ran to the end of the verified frames, never when the
            // caller stopped enumerating early: the next append goes here.
            _appendOffset = verifiedEnd;
            _tailUnchecked = true;
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

        // Nothing is left of a partial frame a failed append could not remove.
        _faulted = false;
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
