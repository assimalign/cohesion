using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Storage.Internal;

using Assimalign.Cohesion.Database.Storage.Units;

/// <summary>
/// The storage's debug consistency check (#1253), PostgreSQL's <c>wal_consistency_checking</c>
/// moved into the writing process: it replays every page record the storage journals onto a
/// shadow copy of the page, exactly as recovery would, and compares the shadow with the page in
/// the buffer pool wherever the two must agree. Off unless the
/// <c>COHESION_STORAGE_CONSISTENCY_CHECKS</c> environment variable is <c>1</c> or <c>true</c>, or a
/// test turns it on; it costs a page copy per page imaged since the checkpoint, a compare per page
/// record, and a read of the data file's copy per imaging touch.
/// </summary>
/// <remarks>
/// <para>
/// <b>What it proves.</b> Recovery rebuilds a page from its full page image and the committed
/// deltas after it, and a delta encodes only what changed since the pre-image its transaction
/// captured. The two agree only when nothing changes a page between its records except a storage
/// transaction that touched it. A change made outside one (a write through a pinned handle with
/// no transaction, a page the storage itself rewrites without journaling) is invisible to every
/// later delta and would be lost, or would corrupt the page, at the next recovery. The check
/// catches it at the first moment it can:
/// </para>
/// <list type="bullet">
/// <item><description>
/// On a page with a record since the redo point (it has a shadow): when the next transaction
/// touches the page without imaging it, its pre-image must equal the shadow; after every commit
/// and rollback the page must equal its replayed shadow; a checkpoint compares every shadow with
/// its page before the truncation discards the records they were built from.
/// </description></item>
/// <item><description>
/// On a page with no record since the redo point (its LSN is at or below it): only a transaction's
/// touch can change such a page, and that touch first journals an image and stamps the page above
/// the redo point. So a page at or below the redo point must be clean: a write-back of one is
/// refused (a checkpoint would otherwise write the change to the data file, where nothing in the
/// journal describes it), and the touch that images one compares the pool's copy with the data
/// file's first (the image would otherwise carry the change as if it were committed).
/// </description></item>
/// </list>
/// <para>
/// Pages the storage writes outside the journal on purpose — allocation's clear (an allocating
/// transaction images the page at its touch; this also covers a new file set's first page and
/// the page manager's raw <c>AllocatePage</c>), the checkpoint anchor pages, the page manager's raw
/// <c>FreePage</c> — are reported through <see cref="Forget"/> and exempt from the second audit
/// until the next checkpoint has written them. A change never marked dirty to a page that is
/// neither touched nor written back again escapes both audits: neither the data file nor the
/// journal ever holds it.
/// </para>
/// <para>
/// <b>Lazy shadows.</b> A page above the redo point whose image this process did not journal (an
/// open that recovered it, then deferred its checkpoint for the engine's scrub) gets its shadow by
/// replaying its records from the journal itself, which also checks invariant P: such a page must
/// have a full page image in the journal.
/// </para>
/// <para>
/// PostgreSQL logs a full-page image with every record of a resource manager under
/// <c>wal_consistency_checking</c> (<c>XLogRecordAssemble</c>,
/// <c>src/backend/access/transam/xloginsert.c:653-654, 717-720</c>) and, after replaying the record,
/// compares the replayed page with that image (<c>verifyBackupPageConsistency</c>,
/// <c>src/backend/access/transam/xlogrecovery.c:2452-2551</c>), failing with "inconsistent page
/// found".
/// </para>
/// </remarks>
internal sealed class StorageConsistencyCheck
{
    /// <summary>
    /// The environment variable that turns the check on for every storage of the process.
    /// </summary>
    internal const string EnvironmentVariable = "COHESION_STORAGE_CONSISTENCY_CHECKS";

    private readonly object _sync = new();
    private readonly Dictionary<long, byte[]> _shadows = new();
    private readonly Dictionary<long, Dictionary<long, byte[]>> _pending = new();

    // The pages written outside the journal on purpose since every page was last written back
    // (see Forget): exempt from the audits of pages at or below the redo point.
    private readonly HashSet<long> _unjournaled = new();
    private readonly StorageJournal _journal;
    private long _checks;

    internal StorageConsistencyCheck(StorageJournal journal)
    {
        _journal = journal;
    }

    /// <summary>
    /// Gets whether the environment asks for the check.
    /// </summary>
    internal static bool RequestedByEnvironment { get; } =
        Environment.GetEnvironmentVariable(EnvironmentVariable) is { } value
        && (value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Gets the number of comparisons made so far (tests).
    /// </summary>
    internal long Checks
    {
        get
        {
            lock (_sync)
            {
                return _checks;
            }
        }
    }

    /// <summary>
    /// Gets the number of pages with a shadow (tests).
    /// </summary>
    internal int ShadowCount
    {
        get
        {
            lock (_sync)
            {
                return _shadows.Count;
            }
        }
    }

    /// <summary>
    /// Replays a journaled full page image onto the page's shadow: the shadow becomes the image.
    /// </summary>
    internal void ImageJournaled(long pageId, long lsn, ReadOnlySpan<byte> runs)
    {
        var shadow = new byte[Page.Size];
        Apply(pageId, lsn, JournalRecordType.FullPageImage, runs, shadow);
        lock (_sync)
        {
            _shadows[pageId] = shadow;
        }
    }

    /// <summary>
    /// The audit: a transaction touches a page above the redo point without imaging it, so its
    /// pre-image is the base of the page's next delta and must equal what recovery rebuilds.
    /// </summary>
    /// <param name="pageId">The page.</param>
    /// <param name="page">The page as the transaction found it.</param>
    internal void CheckUnimagedTouch(long pageId, ReadOnlySpan<byte> page)
    {
        var shadow = GetOrRebuild(pageId, BinaryPrimitives.ReadInt64LittleEndian(page[Page.LsnFieldOffset..]));
        Compare(pageId, shadow, page, compareLsn: true, "was changed outside a storage transaction since its last journal record");
    }

    /// <summary>
    /// The audit of a touch that images a page at or below the redo point: the image carries the
    /// page as the pool holds it, and nothing but a storage transaction's touch may have changed
    /// it since the last checkpoint wrote it, so the pool's copy must equal the data file's.
    /// </summary>
    /// <param name="pageId">The page.</param>
    /// <param name="page">The page as the transaction found it.</param>
    /// <param name="readStored">Reads the data file's copy of a page; false when the file does not reach it.</param>
    internal void CheckImagingTouch(long pageId, ReadOnlySpan<byte> page, Func<long, byte[], bool> readStored)
    {
        lock (_sync)
        {
            if (_unjournaled.Contains(pageId))
            {
                return;
            }
        }

        var stored = new byte[Page.Size];
        if (readStored(pageId, stored))
        {
            Compare(
                pageId,
                stored,
                page,
                compareLsn: true,
                "was changed outside a storage transaction since the last checkpoint, and the full page image this touch journals would carry the change as committed content",
                reference: "the data file holds");
        }
    }

    /// <summary>
    /// The audit of a write-back: a page at or below the redo point has no journal record since
    /// the last checkpoint, which wrote it, and a storage transaction's touch stamps a page above
    /// the redo point before it changes it. So a dirty page at or below it was changed outside a
    /// storage transaction, and writing it back would put a change no record describes into the
    /// data file.
    /// </summary>
    /// <param name="pageId">The page being written back.</param>
    /// <param name="pageLsn">The LSN of the copy being written.</param>
    /// <param name="redoLsn">The storage's redo point.</param>
    internal void CheckWriteBack(long pageId, long pageLsn, long redoLsn)
    {
        if (pageLsn > redoLsn)
        {
            return;
        }

        lock (_sync)
        {
            _checks++;
            if (_unjournaled.Contains(pageId))
            {
                return;
            }
        }

        throw Inconsistent(
            pageId,
            $"it is written back with LSN {pageLsn}, at or below the redo point {redoLsn}, so it was changed outside a storage " +
            "transaction since the last checkpoint, and no journal record holds the change");
    }

    /// <summary>
    /// Every page was just written back (a new storage's first flush): pages written outside the
    /// journal before now are on the data file, so they leave the exemption.
    /// </summary>
    internal void AllWrittenBack()
    {
        lock (_sync)
        {
            _unjournaled.Clear();
        }
    }

    /// <summary>
    /// Replays a commit's page record (a delta or a committed image) onto a copy of the page's
    /// shadow and compares the result with the page. The copy replaces the shadow only once the
    /// transaction's commit record is appended (<see cref="CommitRecordAppended"/>): recovery
    /// applies the record only then.
    /// </summary>
    /// <param name="sequence">The committing transaction.</param>
    /// <param name="pageId">The page.</param>
    /// <param name="type">The record type.</param>
    /// <param name="lsn">The record's LSN.</param>
    /// <param name="payload">The record's payload.</param>
    /// <param name="page">The page as the transaction leaves it, before its LSN is stamped.</param>
    internal void CommitRecordJournaled(long sequence, long pageId, JournalRecordType type, long lsn, ReadOnlySpan<byte> payload, ReadOnlySpan<byte> page)
    {
        var shadow = (byte[])GetOrRebuild(pageId, BinaryPrimitives.ReadInt64LittleEndian(page[Page.LsnFieldOffset..])).Clone();
        long baseLsn = BinaryPrimitives.ReadInt64LittleEndian(payload);
        long shadowLsn = BinaryPrimitives.ReadInt64LittleEndian(shadow.AsSpan(Page.LsnFieldOffset));
        if (baseLsn != shadowLsn)
        {
            throw Inconsistent(pageId, $"its {type} at LSN {lsn} follows LSN {baseLsn}, but recovery would rebuild the page at LSN {shadowLsn}");
        }

        Apply(pageId, lsn, type, payload[PageImageCodec.BaseLsnSize..], shadow);
        Compare(pageId, shadow, page, compareLsn: false, $"differs from what recovery rebuilds from its {type} at LSN {lsn}");
        lock (_sync)
        {
            if (!_pending.TryGetValue(sequence, out var pages))
            {
                pages = new Dictionary<long, byte[]>();
                _pending[sequence] = pages;
            }

            pages[pageId] = shadow;
        }
    }

    /// <summary>
    /// The transaction's commit record is in the journal: the shadows its page records produced
    /// become the pages' shadows.
    /// </summary>
    internal void CommitRecordAppended(long sequence)
    {
        lock (_sync)
        {
            if (_pending.Remove(sequence, out var pages))
            {
                foreach (var (pageId, shadow) in pages)
                {
                    _shadows[pageId] = shadow;
                }
            }
        }
    }

    /// <summary>
    /// The transaction ends without a commit record (a rollback, or a commit that failed before
    /// its record): recovery applies none of its page records, so its shadows are dropped.
    /// </summary>
    internal void Abandon(long sequence)
    {
        lock (_sync)
        {
            _pending.Remove(sequence);
        }
    }

    /// <summary>
    /// A committed transaction touched the page and left it unchanged, so it journaled nothing for
    /// it: the page must still equal its shadow.
    /// </summary>
    internal void CommittedUnchanged(long pageId, ReadOnlySpan<byte> page)
    {
        var shadow = GetOrRebuild(pageId, BinaryPrimitives.ReadInt64LittleEndian(page[Page.LsnFieldOffset..]));
        Compare(pageId, shadow, page, compareLsn: false, "was left unchanged by its committed transaction, yet differs from what recovery rebuilds");
    }

    /// <summary>
    /// A rollback restored the page from its pre-image, keeping the LSN of the page's last record:
    /// it must equal its shadow again.
    /// </summary>
    internal void RolledBack(long pageId, ReadOnlySpan<byte> page)
    {
        var shadow = GetOrRebuild(pageId, BinaryPrimitives.ReadInt64LittleEndian(page[Page.LsnFieldOffset..]));
        Compare(pageId, shadow, page, compareLsn: true, "differs after its rollback from what recovery rebuilds");
    }

    /// <summary>
    /// The storage wrote the page outside the journal on purpose — allocation's clear, a checkpoint
    /// anchor page (which recovery never replays onto), the page manager's raw free: its shadow no
    /// longer describes it, and it is exempt from the audits of pages at or below the redo point
    /// until the next checkpoint writes it. An allocating transaction images the page at its touch;
    /// any other transaction to touch it later finds it allocated afresh, with LSN zero, and images it.
    /// </summary>
    internal void Forget(long pageId)
    {
        lock (_sync)
        {
            _shadows.Remove(pageId);
            _unjournaled.Add(pageId);
        }
    }

    /// <summary>
    /// A checkpoint wrote every page back and is about to truncate the journal: every shadow is
    /// compared with its page, then all are dropped, since every page is imaged again after the
    /// checkpoint, and the pages written outside the journal leave the exemption, since the data
    /// file now holds them.
    /// </summary>
    /// <param name="readPage">Reads a page as the storage holds it now: from the buffer pool, or the data file.</param>
    internal void Checkpointing(Action<long, byte[]> readPage)
    {
        KeyValuePair<long, byte[]>[] shadows;
        lock (_sync)
        {
            shadows = [.. _shadows];
            _shadows.Clear();
            _unjournaled.Clear();
        }

        var page = new byte[Page.Size];
        foreach (var (pageId, shadow) in shadows)
        {
            readPage(pageId, page);
            Compare(pageId, shadow, page, compareLsn: false, "differs at the checkpoint from what recovery rebuilds");
        }
    }

    private byte[] GetOrRebuild(long pageId, long pageLsn)
    {
        lock (_sync)
        {
            if (_shadows.TryGetValue(pageId, out var shadow))
            {
                return shadow;
            }
        }

        var rebuilt = Rebuild(pageId, pageLsn);
        lock (_sync)
        {
            _shadows[pageId] = rebuilt;
        }

        return rebuilt;
    }

    /// <summary>
    /// Replays the page's records from the journal, as recovery does: its images, and the deltas
    /// and committed images of committed transactions, each on its base LSN.
    /// </summary>
    private byte[] Rebuild(long pageId, long pageLsn)
    {
        var committed = new HashSet<long>();
        foreach (var record in _journal.ReadSequential())
        {
            if (record.Type == JournalRecordType.CommitTransaction)
            {
                committed.Add(record.TransactionSequence);
            }
        }

        byte[]? shadow = null;
        foreach (var record in _journal.ReadSequential())
        {
            if ((long)record.PageId != pageId || !StorageRecovery.IsPageRecord(record.Type))
            {
                continue;
            }

            if (record.Type == JournalRecordType.FullPageImage)
            {
                shadow ??= new byte[Page.Size];
                Apply(pageId, record.Lsn, record.Type, record.Payload.Span, shadow);
            }
            else if (committed.Contains(record.TransactionSequence))
            {
                if (shadow is null)
                {
                    throw Inconsistent(pageId, $"its committed {record.Type} at LSN {record.Lsn} has no full page image before it in the journal");
                }

                long baseLsn = BinaryPrimitives.ReadInt64LittleEndian(record.Payload.Span);
                long shadowLsn = BinaryPrimitives.ReadInt64LittleEndian(shadow.AsSpan(Page.LsnFieldOffset));
                if (baseLsn != shadowLsn)
                {
                    throw Inconsistent(pageId, $"its committed {record.Type} at LSN {record.Lsn} follows LSN {baseLsn}, but the journal rebuilds the page at LSN {shadowLsn}");
                }

                Apply(pageId, record.Lsn, record.Type, record.Payload.Span[PageImageCodec.BaseLsnSize..], shadow);
            }
        }

        return shadow ?? throw Inconsistent(
            pageId,
            $"it carries LSN {pageLsn}, above the redo point, but the journal holds no full page image of it (invariant P)");
    }

    private static void Apply(long pageId, long lsn, JournalRecordType type, ReadOnlySpan<byte> runs, Span<byte> shadow)
    {
        string? problem = type == JournalRecordType.PageDelta
            ? PageImageCodec.TryApplyRuns(runs, shadow)
            : PageImageCodec.TryApplyImage(runs, shadow);
        if (problem is not null)
        {
            throw Inconsistent(pageId, $"its {type} at LSN {lsn} does not decode: {problem}");
        }

        BinaryPrimitives.WriteInt64LittleEndian(shadow[Page.LsnFieldOffset..], lsn);
    }

    private void Compare(
        long pageId,
        ReadOnlySpan<byte> expected,
        ReadOnlySpan<byte> page,
        bool compareLsn,
        string what,
        string reference = "recovery rebuilds")
    {
        lock (_sync)
        {
            _checks++;
        }

        int difference = PageImageCodec.FirstDifference(expected, page);
        if (difference >= 0)
        {
            throw Inconsistent(pageId, $"it {what}: the first differing byte is at offset {difference} (0x{page[difference]:X2}, {reference} 0x{expected[difference]:X2})");
        }

        long expectedLsn = BinaryPrimitives.ReadInt64LittleEndian(expected[Page.LsnFieldOffset..]);
        long pageLsn = BinaryPrimitives.ReadInt64LittleEndian(page[Page.LsnFieldOffset..]);
        if (compareLsn && expectedLsn != pageLsn)
        {
            throw Inconsistent(pageId, $"it {what}: it carries LSN {pageLsn}, but {reference} LSN {expectedLsn}, which its next record must follow");
        }
    }

    private static InvalidOperationException Inconsistent(long pageId, string detail)
        => new($"Storage consistency check failed for page {pageId}: {detail}.");
}
