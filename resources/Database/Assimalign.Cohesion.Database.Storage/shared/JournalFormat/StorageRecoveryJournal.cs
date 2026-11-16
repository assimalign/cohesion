using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

using Assimalign.Cohesion.FileSystem;

// Deviates from the repo RootNamespace rule: JournalFormat codec and analysis are compiled
// into Storage and each engine without exposing a public implementation bridge.
namespace Assimalign.Cohesion.Database.Storage.Internal;

internal static class StorageRecoveryJournal
{
    internal const long RecoveryOffsetMask = 0x0000FFFFFFFFFFFFL;
    internal const int RecoveryLengthShift = 48;
    internal const int RecoveryMaximumFrameLength = ushort.MaxValue;
    internal static bool IsPageRecord(JournalRecordType type)
        => type is JournalRecordType.FullPageImage or JournalRecordType.PageDelta or JournalRecordType.CommittedPageImage;

    // A mismatched frame delegates to the full probe, preserving header/format refusal order.
    internal static bool RequiresRecovery(StorageStream journal, long dataLength, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // A full image of an existing page must be restored regardless of commit. The first
        // such image is enough to require a probe; only the no-image path needs planning.
        foreach (var frame in ReadFrames(journal, readAhead: false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (frame.Span[0] != currentVersion) { return true; }
            var record = DecodeRecord(frame);
            long pageId = (long)record.PageId;
            if (record.Type == JournalRecordType.FullPageImage && pageId > 0
                && pageId < dataLength / Units.Page.Size) { return true; }
        }
        bool unsupported = false;
        IEnumerable<JournalRecord> Records()
        {
            foreach (var frame in ReadFrames(journal))
            {
                var body = frame.Span;
                if (body[0] != currentVersion) { unsupported = true; yield break; }
                yield return DecodeRecord(frame);
            }
        }
        var analysis = Analyze(Records, cancellationToken);
        if (unsupported) { return true; }
        foreach (var (pageId, plan) in analysis.Pages)
        {
            if (plan.Committed || (pageId + 1) * Units.Page.Size <= dataLength) { return true; }
        }
        return false;
    }

    // The caller checks the verified frame version before decoding the fixed header.
    internal static JournalRecord DecodeRecord(ReadOnlyMemory<byte> frame)
    {
        var body = frame.Span;
        return new JournalRecord(BinaryPrimitives.ReadInt64LittleEndian(body[1..]),
            BinaryPrimitives.ReadInt64LittleEndian(body[9..]), (JournalRecordType)body[17],
            (PageId)BinaryPrimitives.ReadInt64LittleEndian(body[18..]), frame[bodyHeaderSize..]);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static StorageRecoveryAnalysis Analyze(Func<IEnumerable<JournalRecord>> readRecords, CancellationToken cancellationToken)
    {
        long lastLsn = 0;
        long maxSequence = 0;
        long checkpointLsn = 0;
        long ordinal = 0;
        var committed = new HashSet<long>();
        var pages = new Dictionary<long, StorageRecoveryPagePlan>();
        var pending = new Dictionary<long, Dictionary<long, StorageRecoveryPagePlan>>();
        var freePending = new Stack<Dictionary<long, StorageRecoveryPagePlan>>();
        var rolledBack = new HashSet<long>();
        bool replan = false;

        void Merge(long pageId, StorageRecoveryPagePlan next)
        {
            pages.TryGetValue(pageId, out var previous);
            pages[pageId] = next.Ordinal >= previous.Ordinal
                ? next with { Committed = previous.Committed || next.Committed, RecordCount = checked(previous.RecordCount + next.RecordCount) }
                : previous with { Committed = previous.Committed || next.Committed, RecordCount = checked(previous.RecordCount + next.RecordCount) };
        }

        foreach (var record in readRecords())
        {
            cancellationToken.ThrowIfCancellationRequested();
            ordinal++;
            lastLsn = record.Lsn;
            maxSequence = Math.Max(maxSequence, record.TransactionSequence);
            if (record.Type == JournalRecordType.CommitTransaction)
            {
                replan |= rolledBack.Contains(record.TransactionSequence);
                committed.Add(record.TransactionSequence);
                if (pending.Remove(record.TransactionSequence, out var changes))
                {
                    foreach (var (pageId, plan) in changes)
                    {
                        Merge(pageId, plan with { Committed = true });
                    }
                    changes.Clear();
                    freePending.Push(changes);
                }
            }
            else if (record.Type == JournalRecordType.RollbackTransaction)
            {
                if (pending.Remove(record.TransactionSequence, out var changes))
                {
                    rolledBack.Add(record.TransactionSequence);
                    changes.Clear();
                    freePending.Push(changes);
                }
            }
            else if (record.Type == JournalRecordType.Checkpoint)
            {
                checkpointLsn = Math.Max(checkpointLsn, record.Lsn);
            }
            else if (IsPageRecord(record.Type) && (long)record.PageId > 0)
            {
                long pageId = (long)record.PageId;
                bool committedRecord = committed.Contains(record.TransactionSequence);
                var plan = new StorageRecoveryPagePlan(ordinal, record.Lsn, committedRecord);
                if (record.Type == JournalRecordType.FullPageImage || committedRecord)
                {
                    Merge(pageId, plan);
                }
                if (!committedRecord)
                {
                    // Retain the last identity and change count per page of an unproven bracket.
                    // A later commit promotes it; missing commits discard it after the scan.
                    if (!pending.TryGetValue(record.TransactionSequence, out var changes))
                    {
                        changes = freePending.TryPop(out var reusable) ? reusable : new Dictionary<long, StorageRecoveryPagePlan>();
                        pending.Add(record.TransactionSequence, changes);
                    }
                    changes.TryGetValue(pageId, out var previous);
                    changes[pageId] = plan with { RecordCount = checked(previous.RecordCount + (record.Type == JournalRecordType.FullPageImage ? 0 : 1)) };
                }
            }
        }
        if (replan)
        {
            // Fresh allocation resumes above MaxSequence, but BeginTransaction(long) and the
            // journal append API permit reusing an adopted sequence after rollback. Base gates
            // every change of that sequence on any commit, even one after the rollback. Rebuild
            // identities on this rare path rather than retaining all rolled-back page entries.
            pages.Clear();
            ordinal = 0;
            foreach (var record in readRecords())
            {
                cancellationToken.ThrowIfCancellationRequested();
                ordinal++;
                bool committedRecord = committed.Contains(record.TransactionSequence);
                if (IsPageRecord(record.Type) && (long)record.PageId > 0
                    && (record.Type == JournalRecordType.FullPageImage || committedRecord))
                {
                    Merge((long)record.PageId, new StorageRecoveryPagePlan(ordinal, record.Lsn, committedRecord));
                }
            }
        }
        return new StorageRecoveryAnalysis(lastLsn, maxSequence, checkpointLsn, committed, pages);
    }

    internal static IEnumerable<ReadOnlyMemory<byte>> ReadFrames(IFileSystemFileHandle handle, long verifiedPrefix = 0, bool readAhead = true)
    {
        byte[] chunk = [];

        try
        {
            long length = handle.Length;
            // Trust is single-use and confined to recovery immediately after its analysis on
            // this instance. Public readers and every later borrowed scan verify every CRC.
            chunk = ArrayPool<byte>.Shared.Rent((int)Math.Min(readChunkSize, Math.Max(length, framePrefixSize)));
            long chunkOffset = 0;
            int chunkCount = 0;
            long position = 0;

            while (position + framePrefixSize <= length)
            {
                if (!Fill(handle, ref chunk, ref chunkOffset, ref chunkCount, position, framePrefixSize, length, readAhead))
                {
                    break;
                }

                int start = (int)(position - chunkOffset);
                int bodyLength = BinaryPrimitives.ReadInt32LittleEndian(chunk.AsSpan(start));
                int magic = BinaryPrimitives.ReadInt32LittleEndian(chunk.AsSpan(start + sizeof(int)));
                if (magic != magicValue || bodyLength < bodyHeaderSize || bodyLength > int.MaxValue - framePrefixSize - sizeof(uint)
                    || (long)framePrefixSize + bodyLength + sizeof(uint) > length - position)
                {
                    break;
                }

                int frameLength = framePrefixSize + bodyLength + sizeof(uint);
                if (!Fill(handle, ref chunk, ref chunkOffset, ref chunkCount, position, frameLength, length, readAhead))
                {
                    break;
                }

                start = (int)(position - chunkOffset);
                var body = chunk.AsMemory(start + framePrefixSize, bodyLength);
                uint expected = BinaryPrimitives.ReadUInt32LittleEndian(chunk.AsSpan(start + framePrefixSize + bodyLength));
                if (position + frameLength > verifiedPrefix && Crc32C.Compute(body.Span) != expected)
                {
                    break;
                }

                position += frameLength;
                yield return body;
            }

        }
        finally
        {
            if (chunk.Length > 0)
            {
                ArrayPool<byte>.Shared.Return(chunk);
            }
        }
    }

    internal const int readChunkSize = 256 * 1024;

    internal static bool Fill(IFileSystemFileHandle handle, ref byte[] chunk, ref long chunkOffset, ref int chunkCount, long position, int count, long length, bool readAhead = true)
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
            byte[] grown = ArrayPool<byte>.Shared.Rent(count);
            ArrayPool<byte>.Shared.Return(chunk);
            chunk = grown;
        }

        int wanted = readAhead ? (int)Math.Min(chunk.Length, length - position) : count;
        int total = 0;
        while (total < wanted)
        {
            int read = handle.Read(chunk.AsSpan(total, wanted - total), position + total);
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

    /// <summary>
    /// Size of the frame prefix: length + magic.
    /// </summary>
    internal const int framePrefixSize = sizeof(int) + sizeof(int);

    /// <summary>
    /// Size of the fixed body header: version + lsn + transaction sequence + type + page id.
    /// </summary>
    internal const int bodyHeaderSize = 1 + sizeof(long) + sizeof(long) + 1 + sizeof(long);

    /// <summary>
    /// Journal frame format version: 4 since storage format 3 (#1253), whose page records are
    /// full page images, page deltas and committed page images, encoded as byte runs; 3 was
    /// storage format 2's (CRC-32C frames carrying full before- and after-images, #1251). A
    /// verified frame of any other version is refused with <see cref="StorageFormatException"/>.
    /// </summary>
    internal const byte currentVersion = 4;

    /// <summary>
    /// Journal frame magic value ('WAL2').
    /// </summary>
    internal const int magicValue = 0x324C4157;

}
