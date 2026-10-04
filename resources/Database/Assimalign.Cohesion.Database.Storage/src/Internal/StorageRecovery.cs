using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Storage.Internal;

using Assimalign.Cohesion.Database.Storage.Internal;
using Assimalign.Cohesion.Database.Storage.Units;

/// <summary>
/// Startup recovery: replays the write-ahead log against the data stream so the
/// file reflects exactly the committed transaction history.
/// </summary>
/// <remarks>
/// <para>
/// Because pages are journaled as full images and each page is write-locked to a
/// single transaction at a time, the desired final state of a page is the image of
/// the <b>last</b> journal record on that page among committed after-images and
/// uncommitted before-images: a committed after-image redoes changes that never
/// reached the file (no-force), and an uncommitted before-image undoes stolen writes
/// that reached it early (steal).
/// </para>
/// <para>
/// Replay is idempotent: an image is applied only when the on-disk page does not
/// already verify (checksum) at exactly the target LSN — after-images stamp the LSN
/// of their record, before-images restore the LSN embedded in the pre-transaction
/// image. LSNs never repeat across a reopen (the storage seeds the journal from the
/// LSN floor its header persists), so an image's LSN can never collide with a stale
/// page's.
/// </para>
/// <para>
/// The pages of the checkpoint anchor chain the newest header slot reads are never
/// replayed onto. They are written outside the journal, and only while no transaction
/// can touch them, so any image of them in the journal is from an earlier life of the
/// page — typically the committed free that returned it to the allocator before a header
/// write reused it — and replaying it would destroy the anchor that open has just read.
/// </para>
/// </remarks>
internal static class StorageRecovery
{
    /// <summary>
    /// Runs recovery and returns the highest transaction sequence observed in the
    /// journal (zero when the journal is empty).
    /// </summary>
    /// <param name="data">The data stream to replay onto.</param>
    /// <param name="journal">The journal to replay.</param>
    /// <param name="forceDurable">Whether the final flush must be durable.</param>
    /// <param name="protectedPages">Pages that hold the live checkpoint anchor chain; never replayed onto.</param>
    internal static long Run(StorageStream data, IStorageJournal journal, bool forceDurable, IReadOnlySet<long>? protectedPages = null)
    {
        long maxSequence = 0;

        var committed = new HashSet<long>();

        IEnumerable<JournalRecord> ReadRecords() => journal is StorageJournal streaming
            ? streaming.ReadSequential()
            : journal.ReadAll();

        foreach (var record in ReadRecords())
        {
            if (record.TransactionSequence > maxSequence)
            {
                maxSequence = record.TransactionSequence;
            }

            if (record.Type == JournalRecordType.CommitTransaction)
            {
                committed.Add(record.TransactionSequence);
            }
        }

        // Keep only each winning image's LSN, never its payload. Recovery memory
        // follows page/transaction identities, not the size of stored content.
        var winners = new Dictionary<long, long>();
        foreach (var record in ReadRecords())
        {
            bool relevant = record.Type switch
            {
                JournalRecordType.AfterPageImage => committed.Contains(record.TransactionSequence),
                JournalRecordType.BeforePageImage => !committed.Contains(record.TransactionSequence),
                _ => false,
            };

            if (relevant && record.Payload.Length == Page.Size && protectedPages?.Contains((long)record.PageId) != true)
            {
                winners[(long)record.PageId] = record.Lsn;
            }
        }

        if (winners.Count == 0)
        {
            return maxSequence;
        }

        var diskBuffer = new byte[Page.Size];

        foreach (var record in ReadRecords())
        {
            long pageId = (long)record.PageId;
            if (!winners.TryGetValue(pageId, out long winnerLsn) || winnerLsn != record.Lsn)
            {
                continue;
            }
            var image = record.Payload;

            // After-images stamp their record LSN; before-images restore the
            // pre-transaction LSN embedded in the captured image.
            long targetLsn = record.Type == JournalRecordType.AfterPageImage
                ? record.Lsn
                : BinaryPrimitives.ReadInt64LittleEndian(image.Span.Slice(8, sizeof(long)));

            bool apply = true;

            if ((pageId + 1) * Page.Size <= data.Length)
            {
                data.ReadPage((PageId)pageId, diskBuffer);

                uint storedChecksum = BinaryPrimitives.ReadUInt32LittleEndian(
                    diskBuffer.AsSpan(Page.ChecksumFieldOffset, sizeof(uint)));
                long diskLsn = BinaryPrimitives.ReadInt64LittleEndian(diskBuffer.AsSpan(8, sizeof(long)));

                if (storedChecksum != 0
                    && diskLsn == targetLsn
                    && PageChecksum.Compute(diskBuffer) == storedChecksum)
                {
                    apply = false; // already in the desired state
                }
            }
            else if (record.Type == JournalRecordType.BeforePageImage)
            {
                // The stolen write never reached the data file; nothing to undo.
                apply = false;
            }

            if (!apply)
            {
                continue;
            }

            var buffer = image.ToArray();
            BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(8, sizeof(long)), targetLsn);
            PageChecksum.Stamp(buffer);
            data.WritePage((PageId)pageId, buffer);
        }

        data.Flush(durable: forceDurable);
        return maxSequence;
    }
}
