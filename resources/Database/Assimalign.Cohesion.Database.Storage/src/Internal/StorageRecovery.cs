using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Storage.Internal;

using Assimalign.Cohesion.Database.Storage.Units;

/// <summary>
/// Startup recovery (storage format 3, #1253): ordered, redo-only replay of the journal from the
/// last checkpoint onto the data stream, so the file reflects exactly the committed transaction
/// history.
/// </summary>
/// <remarks>
/// <para>
/// <b>What each page record means.</b> A <see cref="JournalRecordType.FullPageImage"/> is the page
/// as it stood before a transaction's first change to it since the checkpoint: committed content,
/// restored unconditionally whatever became of the transaction that journaled it. A
/// <see cref="JournalRecordType.PageDelta"/> or <see cref="JournalRecordType.CommittedPageImage"/>
/// is a committed transaction's change, appended at its commit, and applies only when that
/// transaction's commit record is in the journal. Records apply in LSN order, which is file order.
/// </para>
/// <para>
/// <b>Why that is complete (invariant P, Storage DESIGN.md "Recovery replay rules").</b> Every page
/// whose LSN is above the last checkpoint's has a full page image in the journal at or after the
/// checkpoint, at or below the page's LSN, so every page a transaction changed since the checkpoint
/// starts from an image here and never from the data file. An uncommitted transaction's stolen
/// bytes on disk are overwritten by the image and the committed changes after it; a page whose
/// write a crash tore is rebuilt the same way. There is no undo pass: rollback is in memory, and a
/// transaction without a commit record has no change recovery applies.
/// </para>
/// <para>
/// <b>Base-LSN chaining.</b> A delta or committed image names the LSN of the record it follows,
/// and recovery applies it only to a page that carries exactly that LSN; anything else is a gap in
/// the page's chain (a lost record, a page changed outside a storage transaction) and fails the open
/// with <see cref="StorageCorruptionException"/> rather than rebuilding a wrong page. So does a
/// committed change with no image before it. Each rebuilt page is written with the LSN of the last
/// record applied to it, which is the base the next transaction's delta names, so a second recovery
/// over a journal that grew after the first one (an engine's open-time scrub runs before its
/// checkpoint) chains through the same LSNs. The page LSN is no longer compared with a target to
/// skip a page: a stolen uncommitted write carries the LSN of the last committed record before it,
/// so the LSN on disk does not identify its content.
/// </para>
/// <para>
/// <b>Memory.</b> Three streaming passes, as before: the committed transactions; the pages the
/// journal rebuilds, with the last record that applies to each; the ordered replay. The replay
/// keeps a page in a bounded cache from its first image to its last record and writes it then; when
/// more pages are open at once than the cache holds, the least recently used is written early and
/// read back (checksum- and LSN-verified) by its next record. Memory therefore follows page
/// identities plus the cache, not the journal's size.
/// </para>
/// <para>
/// A page past the end of the data file that only images of uncommitted transactions describe is
/// not written: its extension never reached the file, so nothing was stolen there.
/// </para>
/// <para>
/// The pages of the checkpoint anchor chain the newest header slot reads are never replayed onto.
/// They are written outside the journal, and only while no transaction can touch them, so any
/// record of them in the journal is from an earlier life of the page, which ended with the free
/// that returned it to the allocator. A header write makes the journal durable through that free's
/// commit record before it writes the chain page (the chain page's LSN makes the buffer pool's
/// write-ahead gate enforce the same order), so the free is committed in the journal recovery reads,
/// and replaying the page's earlier life would destroy the anchor that open has just read.
/// </para>
/// <para>
/// Page 0 is never replayed onto either: it is the file header, written outside the journal and
/// never through a transaction, so a page-0 record in the journal can only be damage, and applying
/// it would roll both header slots back.
/// </para>
/// <para>
/// PostgreSQL restores a full-page image whenever its <c>BKPIMAGE_APPLY</c> flag is set, whatever
/// the page's LSN, and applies a record's own changes only to a page whose LSN is below the record's
/// (<c>XLogReadBufferForRedoExtended</c>, <c>src/backend/access/transam/xlogutils.c:395-447</c>).
/// Its images are post-images of logged changes and it needs no commit gate, because it never undoes
/// a page; Cohesion's images are pre-images and its changes are commit-gated, which is what lets an
/// uncommitted bracket's stolen writes vanish without an undo record.
/// </para>
/// </remarks>
internal static class StorageRecovery
{
    /// <summary>
    /// Runs recovery.
    /// </summary>
    /// <param name="data">The data stream to replay onto.</param>
    /// <param name="journal">The journal to replay.</param>
    /// <param name="forceDurable">Whether the final flush must be durable.</param>
    /// <param name="protectedPages">Pages that hold the live checkpoint anchor chain; never replayed onto.</param>
    /// <param name="cacheCapacity">The most pages the replay holds in memory at once.</param>
    /// <returns>What recovery found and did.</returns>
    /// <exception cref="StorageCorruptionException">A page's chain of records has a gap, or a record does not decode.</exception>
    internal static StorageRecoveryResult Run(
        StorageStream data,
        IStorageJournal journal,
        bool forceDurable,
        IReadOnlySet<long>? protectedPages = null,
        int cacheCapacity = Storage.DefaultBufferPoolCapacity)
    {
        IEnumerable<JournalRecord> ReadRecords() => journal is StorageJournal streaming
            ? streaming.ReadSequential()
            : journal.ReadAll();

        // Pass 1: which transactions committed, the highest sequence, and the checkpoint the
        // journal starts at.
        long maxSequence = 0;
        long checkpointLsn = 0;
        var committed = new HashSet<long>();
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
            else if (record.Type == JournalRecordType.Checkpoint && record.Lsn > checkpointLsn)
            {
                checkpointLsn = record.Lsn;
            }
        }

        // Pass 2: the pages the journal rebuilds — each with the last record that applies to it,
        // when the replay writes it, and whether a committed transaction touched it.
        var plans = new Dictionary<long, PagePlan>();
        foreach (var record in ReadRecords())
        {
            if (!IsPageRecord(record.Type))
            {
                continue;
            }

            long pageId = (long)record.PageId;
            bool committedRecord = committed.Contains(record.TransactionSequence);
            if (pageId <= 0 || protectedPages?.Contains(pageId) == true || (record.Type != JournalRecordType.FullPageImage && !committedRecord))
            {
                continue;
            }

            plans.TryGetValue(pageId, out var plan);
            plans[pageId] = new PagePlan(record.Lsn, plan.Committed || committedRecord);
        }

        long originalLength = data.Length;
        var skipped = new List<long>();
        foreach (var (pageId, plan) in plans)
        {
            if (!plan.Committed && (pageId + 1) * Page.Size > originalLength)
            {
                skipped.Add(pageId);
            }
        }

        foreach (long pageId in skipped)
        {
            plans.Remove(pageId);
        }

        if (plans.Count == 0)
        {
            return new StorageRecoveryResult(maxSequence, checkpointLsn, 0);
        }

        // Pass 3: the ordered replay.
        var cache = new RedoCache(data, Math.Max(1, cacheCapacity));
        var imaged = new HashSet<long>();
        foreach (var record in ReadRecords())
        {
            if (!IsPageRecord(record.Type) || !plans.TryGetValue((long)record.PageId, out var plan))
            {
                continue;
            }

            long pageId = (long)record.PageId;
            var payload = record.Payload.Span;
            byte[] page;
            string? problem;

            if (record.Type == JournalRecordType.FullPageImage)
            {
                page = cache.Take(pageId);
                problem = PageImageCodec.TryApplyImage(payload, page);
                imaged.Add(pageId);
            }
            else
            {
                if (!committed.Contains(record.TransactionSequence))
                {
                    continue;
                }

                if (!imaged.Contains(pageId))
                {
                    throw new StorageCorruptionException(
                        record.PageId,
                        $"Recovery cannot rebuild page {pageId}: the committed {Describe(record.Type)} at LSN {record.Lsn} has no full page " +
                        "image before it in the journal, so the page's chain of records has a gap.");
                }

                page = cache.Get(pageId);
                if (payload.Length < PageImageCodec.BaseLsnSize)
                {
                    throw Malformed(record, "it is shorter than its base LSN");
                }

                long baseLsn = BinaryPrimitives.ReadInt64LittleEndian(payload);
                long pageLsn = BinaryPrimitives.ReadInt64LittleEndian(page.AsSpan(Page.LsnFieldOffset));
                if (pageLsn != baseLsn)
                {
                    throw new StorageCorruptionException(
                        record.PageId,
                        $"Recovery cannot rebuild page {pageId}: the committed {Describe(record.Type)} at LSN {record.Lsn} follows LSN {baseLsn}, " +
                        $"but the page rebuilt so far carries LSN {pageLsn}, so the page's chain of records has a gap.");
                }

                var runs = payload[PageImageCodec.BaseLsnSize..];
                problem = record.Type == JournalRecordType.PageDelta
                    ? PageImageCodec.TryApplyRuns(runs, page)
                    : PageImageCodec.TryApplyImage(runs, page);
            }

            if (problem is not null)
            {
                throw Malformed(record, problem);
            }

            // Every rebuilt page carries the LSN of the last record applied to it: the base the
            // next transaction's delta names.
            BinaryPrimitives.WriteInt64LittleEndian(page.AsSpan(Page.LsnFieldOffset), record.Lsn);

            if (record.Lsn == plan.LastLsn)
            {
                cache.Retire(pageId);
            }
        }

        cache.WriteAll();
        data.Flush(durable: forceDurable);
        return new StorageRecoveryResult(maxSequence, checkpointLsn, plans.Count);
    }

    /// <summary>
    /// Gets whether a record type describes a page (storage format 3).
    /// </summary>
    internal static bool IsPageRecord(JournalRecordType type)
        => type is JournalRecordType.FullPageImage or JournalRecordType.PageDelta or JournalRecordType.CommittedPageImage;

    private static string Describe(JournalRecordType type) => type switch
    {
        JournalRecordType.FullPageImage => "full page image",
        JournalRecordType.PageDelta => "page delta",
        _ => "committed page image",
    };

    private static StorageCorruptionException Malformed(JournalRecord record, string problem)
        => new(
            record.PageId,
            $"Recovery cannot apply the {Describe(record.Type)} of page {(long)record.PageId} at LSN {record.Lsn}: {problem}.");

    /// <summary>
    /// What recovery does to a page: the LSN of the last record that applies to it, and whether a
    /// committed transaction's record touches it.
    /// </summary>
    private readonly record struct PagePlan(long LastLsn, bool Committed);

    /// <summary>
    /// The pages the replay is rebuilding, at most a capacity of them in memory: a page enters with
    /// its first image and leaves when its last record is applied (<see cref="Retire"/>), written to
    /// the data stream with a fresh checksum. Past the capacity the least recently used page is
    /// written early and read back, verified, when its next record needs it.
    /// </summary>
    private sealed class RedoCache
    {
        private readonly StorageStream _data;
        private readonly int _capacity;
        private readonly Dictionary<long, LinkedListNode<(long PageId, byte[] Buffer)>> _entries = new();
        private readonly LinkedList<(long PageId, byte[] Buffer)> _order = new();
        private readonly Stack<byte[]> _free = new();

        internal RedoCache(StorageStream data, int capacity)
        {
            _data = data;
            _capacity = capacity;
        }

        /// <summary>
        /// Returns the page's buffer for a full page image to overwrite, without reading the page.
        /// </summary>
        internal byte[] Take(long pageId)
        {
            if (_entries.TryGetValue(pageId, out var node))
            {
                Touch(node);
                return node.Value.Buffer;
            }

            return Add(pageId).Buffer;
        }

        /// <summary>
        /// Returns the page as the replay left it: cached, or read back from the data stream, where
        /// the replay wrote it when it was evicted.
        /// </summary>
        internal byte[] Get(long pageId)
        {
            if (_entries.TryGetValue(pageId, out var node))
            {
                Touch(node);
                return node.Value.Buffer;
            }

            var entry = Add(pageId);
            _data.ReadPage((PageId)pageId, entry.Buffer);
            PageChecksum.Verify(entry.Buffer, (PageId)pageId);
            return entry.Buffer;
        }

        /// <summary>
        /// Writes the page and drops it from the cache: its last record was applied.
        /// </summary>
        internal void Retire(long pageId)
        {
            if (_entries.Remove(pageId, out var node))
            {
                _order.Remove(node);
                Write(node.Value.PageId, node.Value.Buffer);
                _free.Push(node.Value.Buffer);
            }
        }

        /// <summary>
        /// Writes every page still cached.
        /// </summary>
        internal void WriteAll()
        {
            foreach (var (pageId, buffer) in _order)
            {
                Write(pageId, buffer);
            }

            _order.Clear();
            _entries.Clear();
        }

        private (long PageId, byte[] Buffer) Add(long pageId)
        {
            if (_entries.Count >= _capacity && _order.First is { } oldest)
            {
                _order.RemoveFirst();
                _entries.Remove(oldest.Value.PageId);
                Write(oldest.Value.PageId, oldest.Value.Buffer);
                _free.Push(oldest.Value.Buffer);
            }

            var buffer = _free.Count > 0 ? _free.Pop() : new byte[Page.Size];
            Array.Clear(buffer);
            var node = _order.AddLast((pageId, buffer));
            _entries[pageId] = node;
            return node.Value;
        }

        private void Touch(LinkedListNode<(long PageId, byte[] Buffer)> node)
        {
            _order.Remove(node);
            _order.AddLast(node);
        }

        private void Write(long pageId, byte[] buffer)
        {
            PageChecksum.Stamp(buffer);
            _data.WritePage((PageId)pageId, buffer);
        }
    }
}

