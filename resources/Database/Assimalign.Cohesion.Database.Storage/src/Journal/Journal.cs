using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Storage;

using Assimalign.Cohesion.Database.Storage.Internal;

/// <summary>
/// Base implementation of the write-ahead log: LSN assignment, append serialization,
/// the CRC-protected frame codec, and torn-tail-tolerant reading. Derived classes
/// provide the physical medium.
/// </summary>
/// <remarks>
/// Frame layout: <c>[int frameLength][int magic][body][uint crc32(body)]</c> where the
/// body is <c>[byte version][long lsn][long transactionSequence][byte type][long pageId]
/// [payload]</c>. A record whose length prefix, magic, or checksum does not verify
/// terminates the read scan — a torn tail belongs to work that was never acknowledged.
/// </remarks>
public abstract class Journal : IJournal
{
    private readonly object _syncRoot = new();
    private long _lastLsn;
    private long _durableLsn;
    private bool _initialized;
    private bool _disposed;

    /// <summary>
    /// Initializes a new journal instance.
    /// </summary>
    protected Journal() { }

    /// <inheritdoc />
    public long LastLsn
    {
        get
        {
            EnsureInitialized();
            return _lastLsn;
        }
    }

    /// <inheritdoc />
    public long DurableLsn
    {
        get
        {
            EnsureInitialized();
            return _durableLsn;
        }
    }

    /// <inheritdoc />
    public long AppendBegin(long transactionSequence)
        => Append(transactionSequence, JournalRecordType.BeginTransaction, default, ReadOnlySpan<byte>.Empty);

    /// <inheritdoc />
    public long AppendPageImage(long transactionSequence, PageId pageId, JournalRecordType type, ReadOnlySpan<byte> image)
    {
        if (type is not (JournalRecordType.BeforePageImage or JournalRecordType.AfterPageImage))
        {
            throw new ArgumentOutOfRangeException(nameof(type), type, "Page image records must be before or after images.");
        }

        return Append(transactionSequence, type, pageId, image);
    }

    /// <inheritdoc />
    public long AppendOperation(long transactionSequence, ReadOnlySpan<byte> payload)
        => Append(transactionSequence, JournalRecordType.Operation, default, payload);

    /// <inheritdoc />
    public long AppendCommit(long transactionSequence)
        => Append(transactionSequence, JournalRecordType.CommitTransaction, default, ReadOnlySpan<byte>.Empty);

    /// <inheritdoc />
    public long AppendRollback(long transactionSequence)
        => Append(transactionSequence, JournalRecordType.RollbackTransaction, default, ReadOnlySpan<byte>.Empty);

    /// <inheritdoc />
    public long Checkpoint(ReadOnlySpan<long> activeTransactions)
    {
        ThrowIfDisposed();
        EnsureInitialized();

        Span<byte> payload = activeTransactions.Length <= 64
            ? stackalloc byte[activeTransactions.Length * sizeof(long)]
            : new byte[activeTransactions.Length * sizeof(long)];

        for (int i = 0; i < activeTransactions.Length; i++)
        {
            BinaryPrimitives.WriteInt64LittleEndian(payload.Slice(i * sizeof(long), sizeof(long)), activeTransactions[i]);
        }

        lock (_syncRoot)
        {
            TruncateCore();
            long lsn = AppendLocked(0, JournalRecordType.Checkpoint, default, payload);
            FlushCore(forceDurable: true);
            _durableLsn = _lastLsn;
            return lsn;
        }
    }

    /// <inheritdoc />
    public void EnsureDurable(long lsn)
    {
        ThrowIfDisposed();
        EnsureInitialized();

        lock (_syncRoot)
        {
            if (_durableLsn >= lsn)
            {
                return;
            }

            FlushCore(forceDurable: true);
            _durableLsn = _lastLsn;
        }
    }

    /// <inheritdoc />
    public void Flush(bool forceDurable = false)
    {
        ThrowIfDisposed();
        EnsureInitialized();

        lock (_syncRoot)
        {
            FlushCore(forceDurable);

            if (forceDurable)
            {
                _durableLsn = _lastLsn;
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<JournalRecord> ReadAll()
    {
        ThrowIfDisposed();
        EnsureInitialized();

        lock (_syncRoot)
        {
            return ReadAllCore();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DisposeCore();
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Dispose();
        return default;
    }

    private long Append(long transactionSequence, JournalRecordType type, PageId pageId, ReadOnlySpan<byte> payload)
    {
        ThrowIfDisposed();
        EnsureInitialized();

        lock (_syncRoot)
        {
            return AppendLocked(transactionSequence, type, pageId, payload);
        }
    }

    private long AppendLocked(long transactionSequence, JournalRecordType type, PageId pageId, ReadOnlySpan<byte> payload)
    {
        long lsn = _lastLsn + 1;

        int bodyLength = BodyHeaderSize + payload.Length;
        byte[] frame = new byte[FramePrefixSize + bodyLength + sizeof(uint)];
        var span = frame.AsSpan();

        BinaryPrimitives.WriteInt32LittleEndian(span, bodyLength);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], Magic);

        var body = span.Slice(FramePrefixSize, bodyLength);
        body[0] = CurrentVersion;
        BinaryPrimitives.WriteInt64LittleEndian(body[1..], lsn);
        BinaryPrimitives.WriteInt64LittleEndian(body[9..], transactionSequence);
        body[17] = (byte)type;
        BinaryPrimitives.WriteInt64LittleEndian(body[18..], (long)pageId);
        payload.CopyTo(body[BodyHeaderSize..]);

        uint checksum = Crc32.Compute(body);
        BinaryPrimitives.WriteUInt32LittleEndian(span[(FramePrefixSize + bodyLength)..], checksum);

        AppendFrame(frame);
        _lastLsn = lsn;
        return lsn;
    }

    private IReadOnlyList<JournalRecord> ReadAllCore()
    {
        var records = new List<JournalRecord>();

        foreach (var frame in ReadFrames())
        {
            var body = frame.Span;

            if (body.Length < BodyHeaderSize || body[0] != CurrentVersion)
            {
                break;
            }

            long lsn = BinaryPrimitives.ReadInt64LittleEndian(body[1..]);
            long transactionSequence = BinaryPrimitives.ReadInt64LittleEndian(body[9..]);
            var type = (JournalRecordType)body[17];
            long pageId = BinaryPrimitives.ReadInt64LittleEndian(body[18..]);
            var payload = frame[BodyHeaderSize..];

            records.Add(new JournalRecord(lsn, transactionSequence, type, (PageId)pageId, payload));
        }

        return records;
    }

    private void EnsureInitialized()
    {
        if (_initialized)
        {
            return;
        }

        lock (_syncRoot)
        {
            if (_initialized)
            {
                return;
            }

            _initialized = true;

            var records = ReadAllCore();
            if (records.Count > 0)
            {
                _lastLsn = records[^1].Lsn;
                _durableLsn = _lastLsn;
            }
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    /// <summary>
    /// Appends a complete encoded frame to the physical medium. Frames must be
    /// persisted in append order.
    /// </summary>
    /// <param name="frame">Encoded frame bytes.</param>
    protected abstract void AppendFrame(ReadOnlySpan<byte> frame);

    /// <summary>
    /// Flushes pending journal bytes to the physical medium.
    /// </summary>
    /// <param name="forceDurable">True when durable (power-safe) flush semantics are required.</param>
    protected abstract void FlushCore(bool forceDurable);

    /// <summary>
    /// Reads all verifiable frame bodies from the physical medium in append order,
    /// stopping at the first frame whose length, magic, or checksum does not verify.
    /// </summary>
    /// <returns>The frame bodies (excluding prefix and checksum).</returns>
    protected abstract IEnumerable<ReadOnlyMemory<byte>> ReadFrames();

    /// <summary>
    /// Discards all persisted journal content. Called under the append lock as the
    /// first half of a checkpoint; the checkpoint record is appended immediately after.
    /// </summary>
    protected abstract void TruncateCore();

    /// <summary>
    /// Releases implementation-specific resources.
    /// </summary>
    protected abstract void DisposeCore();

    /// <summary>
    /// Size of the frame prefix: length + magic.
    /// </summary>
    protected const int FramePrefixSize = sizeof(int) + sizeof(int);

    /// <summary>
    /// Size of the fixed body header: version + lsn + transaction sequence + type + page id.
    /// </summary>
    protected const int BodyHeaderSize = 1 + sizeof(long) + sizeof(long) + 1 + sizeof(long);

    /// <summary>
    /// Journal binary format version.
    /// </summary>
    protected const byte CurrentVersion = 2;

    /// <summary>
    /// Journal frame magic value ('WAL2').
    /// </summary>
    protected const int Magic = 0x324C4157;
}
