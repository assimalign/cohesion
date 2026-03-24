using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Storage;

using Assimalign.Cohesion.Database.Storage.Internal;

/// <summary>
/// Base implementation of a journal logger with ACID-oriented transaction semantics.
/// </summary>
/// <remarks>
/// Atomicity: transactions are delimited by begin/commit/rollback records and recovery replays only committed operations.
/// Consistency: each record uses checksums and sequential LSN ordering; corrupted tail records are ignored.
/// Isolation: append operations are serialized by an internal lock to preserve deterministic log order.
/// Durability: commit and checkpoint force flush to underlying storage.
/// </remarks>
public abstract class JournalLoggerBase : IJournalLogger
{
    private readonly object _syncRoot = new();
    private readonly Dictionary<JournalTransactionId, TransactionContext> _transactions = new();

    private long _nextLsn;
    private bool _initialized;
    private bool _disposed;

    /// <summary>
    /// Initializes a new journal logger instance.
    /// </summary>
    protected JournalLoggerBase() { }

    /// <inheritdoc />
    public JournalTransactionId BeginTransaction(string modelName, string resourceName)
    {
        ThrowIfDisposed();
        EnsureInitialized();

        if (string.IsNullOrWhiteSpace(modelName))
        {
            throw new ArgumentException("Model name cannot be null or empty.", nameof(modelName));
        }

        if (string.IsNullOrWhiteSpace(resourceName))
        {
            throw new ArgumentException("Resource name cannot be null or empty.", nameof(resourceName));
        }

        lock (_syncRoot)
        {
            var transactionId = JournalTransactionId.NewId();
            _transactions[transactionId] = new TransactionContext(modelName, resourceName, TransactionState.Active);
            AppendCore(transactionId, JournalRecordType.BeginTransaction, modelName, resourceName, "BEGIN", ReadOnlySpan<byte>.Empty);
            return transactionId;
        }
    }

    /// <inheritdoc />
    public long AppendOperation(JournalTransactionId transactionId, string operationName, ReadOnlySpan<byte> payload)
    {
        ThrowIfDisposed();
        EnsureInitialized();

        if (string.IsNullOrWhiteSpace(operationName))
        {
            throw new ArgumentException("Operation name cannot be null or empty.", nameof(operationName));
        }

        lock (_syncRoot)
        {
            if (!_transactions.TryGetValue(transactionId, out var context) || context.State != TransactionState.Active)
            {
                throw new JournalException($"Cannot append operation: transaction '{transactionId}' is not active.");
            }

            return AppendCore(transactionId, JournalRecordType.Operation, context.ModelName, context.ResourceName, operationName, payload);
        }
    }

    /// <inheritdoc />
    public void CommitTransaction(JournalTransactionId transactionId)
    {
        ThrowIfDisposed();
        EnsureInitialized();

        lock (_syncRoot)
        {
            if (!_transactions.TryGetValue(transactionId, out var context) || context.State != TransactionState.Active)
            {
                throw new JournalException($"Cannot commit transaction '{transactionId}': transaction is not active.");
            }

            AppendCore(transactionId, JournalRecordType.CommitTransaction, context.ModelName, context.ResourceName, "COMMIT", ReadOnlySpan<byte>.Empty);
            _transactions[transactionId] = context with { State = TransactionState.Committed };
            Flush(forceDurable: true);
        }
    }

    /// <inheritdoc />
    public void RollbackTransaction(JournalTransactionId transactionId)
    {
        ThrowIfDisposed();
        EnsureInitialized();

        lock (_syncRoot)
        {
            if (!_transactions.TryGetValue(transactionId, out var context) || context.State != TransactionState.Active)
            {
                throw new JournalException($"Cannot rollback transaction '{transactionId}': transaction is not active.");
            }

            AppendCore(transactionId, JournalRecordType.RollbackTransaction, context.ModelName, context.ResourceName, "ROLLBACK", ReadOnlySpan<byte>.Empty);
            _transactions[transactionId] = context with { State = TransactionState.RolledBack };
            Flush(forceDurable: true);
        }
    }

    /// <inheritdoc />
    public void Checkpoint()
    {
        ThrowIfDisposed();
        EnsureInitialized();

        lock (_syncRoot)
        {
            AppendCore(default, JournalRecordType.Checkpoint, "SYSTEM", "JOURNAL", "CHECKPOINT", ReadOnlySpan<byte>.Empty);
            Flush(forceDurable: true);
        }
    }

    /// <inheritdoc />
    public void Flush(bool forceDurable = false)
    {
        ThrowIfDisposed();
        EnsureInitialized();
        FlushCore(forceDurable);
    }

    /// <inheritdoc />
    public IReadOnlyList<JournalRecord> ReadAll()
    {
        ThrowIfDisposed();
        EnsureInitialized();
        return ReadAllInternal();
    }

    /// <inheritdoc />
    public IReadOnlyList<JournalRecord> RecoverCommittedOperations()
    {
        ThrowIfDisposed();
        EnsureInitialized();

        var records = ReadAllInternal();
        var states = new Dictionary<JournalTransactionId, TransactionState>();
        var operations = new List<JournalRecord>();

        foreach (var record in records)
        {
            switch (record.RecordType)
            {
                case JournalRecordType.BeginTransaction:
                    states[record.TransactionId] = TransactionState.Active;
                    break;

                case JournalRecordType.Operation:
                    operations.Add(record);
                    break;

                case JournalRecordType.CommitTransaction:
                    states[record.TransactionId] = TransactionState.Committed;
                    break;

                case JournalRecordType.RollbackTransaction:
                    states[record.TransactionId] = TransactionState.RolledBack;
                    break;
            }
        }

        var replay = new List<JournalRecord>(operations.Count);
        for (int i = 0; i < operations.Count; i++)
        {
            var operation = operations[i];
            if (states.TryGetValue(operation.TransactionId, out var state) && state == TransactionState.Committed)
            {
                replay.Add(operation);
            }
        }

        return replay;
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

    private long AppendCore(
        JournalTransactionId transactionId,
        JournalRecordType recordType,
        string modelName,
        string resourceName,
        string operationName,
        ReadOnlySpan<byte> payload)
    {
        long lsn = _nextLsn++;
        DateTimeOffset timestamp = DateTimeOffset.UtcNow;

        byte[] modelBytes = Encoding.UTF8.GetBytes(modelName);
        byte[] resourceBytes = Encoding.UTF8.GetBytes(resourceName);
        byte[] operationBytes = Encoding.UTF8.GetBytes(operationName);
        byte[] payloadBytes = payload.ToArray();

        int bodyLength =
            sizeof(int) + // version
            sizeof(long) + // lsn
            sizeof(long) + // timestamp ticks
            16 + // transaction id
            sizeof(byte) + // record type
            sizeof(int) + modelBytes.Length +
            sizeof(int) + resourceBytes.Length +
            sizeof(int) + operationBytes.Length +
            sizeof(int) + payloadBytes.Length;

        byte[] body = new byte[bodyLength];
        var span = body.AsSpan();
        int offset = 0;

        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(offset, sizeof(int)), CurrentVersion);
        offset += sizeof(int);

        BinaryPrimitives.WriteInt64LittleEndian(span.Slice(offset, sizeof(long)), lsn);
        offset += sizeof(long);

        BinaryPrimitives.WriteInt64LittleEndian(span.Slice(offset, sizeof(long)), timestamp.UtcTicks);
        offset += sizeof(long);

        transactionId.Value.TryWriteBytes(span.Slice(offset, 16));
        offset += 16;

        span[offset] = (byte)recordType;
        offset += sizeof(byte);

        WriteLengthPrefixed(span, ref offset, modelBytes);
        WriteLengthPrefixed(span, ref offset, resourceBytes);
        WriteLengthPrefixed(span, ref offset, operationBytes);
        WriteLengthPrefixed(span, ref offset, payloadBytes);

        uint checksum = Crc32.Compute(body);

        int frameLength =
            sizeof(int) + // magic
            body.Length +
            sizeof(uint); // checksum

        byte[] frame = new byte[sizeof(int) + frameLength];
        var frameSpan = frame.AsSpan();
        int frameOffset = 0;

        BinaryPrimitives.WriteInt32LittleEndian(frameSpan.Slice(frameOffset, sizeof(int)), frameLength);
        frameOffset += sizeof(int);

        BinaryPrimitives.WriteInt32LittleEndian(frameSpan.Slice(frameOffset, sizeof(int)), Magic);
        frameOffset += sizeof(int);

        body.CopyTo(frameSpan.Slice(frameOffset, body.Length));
        frameOffset += body.Length;

        BinaryPrimitives.WriteUInt32LittleEndian(frameSpan.Slice(frameOffset, sizeof(uint)), checksum);

        AppendFrame(frame);
        return lsn;
    }

    private static void WriteLengthPrefixed(Span<byte> destination, ref int offset, ReadOnlySpan<byte> value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(offset, sizeof(int)), value.Length);
        offset += sizeof(int);

        value.CopyTo(destination.Slice(offset, value.Length));
        offset += value.Length;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
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

            var records = ReadAllInternal();
            _nextLsn = records.Count == 0 ? 1 : records[^1].Lsn + 1;

            foreach (var record in records)
            {
                switch (record.RecordType)
                {
                    case JournalRecordType.BeginTransaction:
                        _transactions[record.TransactionId] = new TransactionContext(record.ModelName, record.ResourceName, TransactionState.Active);
                        break;

                    case JournalRecordType.CommitTransaction:
                        if (_transactions.TryGetValue(record.TransactionId, out var committedContext))
                        {
                            _transactions[record.TransactionId] = committedContext with { State = TransactionState.Committed };
                        }
                        break;

                    case JournalRecordType.RollbackTransaction:
                        if (_transactions.TryGetValue(record.TransactionId, out var rolledBackContext))
                        {
                            _transactions[record.TransactionId] = rolledBackContext with { State = TransactionState.RolledBack };
                        }
                        break;
                }
            }

            _initialized = true;
        }
    }

    /// <summary>
    /// Appends a complete encoded frame to persistent storage.
    /// </summary>
    /// <param name="frame">Encoded frame bytes.</param>
    protected abstract void AppendFrame(ReadOnlySpan<byte> frame);

    /// <summary>
    /// Flushes pending journal bytes.
    /// </summary>
    /// <param name="forceDurable">True when durable flush is required.</param>
    protected abstract void FlushCore(bool forceDurable);

    /// <summary>
    /// Reads all valid journal records from storage.
    /// </summary>
    /// <returns>Decoded record list.</returns>
    protected abstract IReadOnlyList<JournalRecord> ReadAllInternal();

    /// <summary>
    /// Releases implementation-specific resources.
    /// </summary>
    protected abstract void DisposeCore();

    /// <summary>
    /// Journal binary format version.
    /// </summary>
    protected const int CurrentVersion = 1;

    /// <summary>
    /// Journal frame magic value ('WAL1').
    /// </summary>
    protected const int Magic = 0x314C4157;

    private enum TransactionState
    {
        Active = 1,
        Committed = 2,
        RolledBack = 3,
    }

    private readonly record struct TransactionContext(string ModelName, string ResourceName, TransactionState State);
}
