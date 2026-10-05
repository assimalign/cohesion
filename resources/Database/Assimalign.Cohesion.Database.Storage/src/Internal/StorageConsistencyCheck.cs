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
/// test turns it on; it costs a page copy per page imaged since the checkpoint and a compare per
/// page record.
/// </summary>
/// <remarks>
/// <para>
/// <b>What it proves.</b> Recovery rebuilds a page from its full page image and the committed
/// deltas after it, and a delta encodes only what changed since the pre-image its transaction
/// captured. The two agree only when nothing changes a page between its records except a storage
/// transaction that touched it. A change made outside one (a write through a pinned handle with
/// no transaction, a page the storage itself rewrites without journaling) is invisible to every
/// later delta and would be lost, or would corrupt the page, at the next recovery. The check
/// catches it at the first moment it can: when the next transaction touches the page without
/// imaging it, its pre-image must equal the shadow (the audit), and after every commit the page
/// must equal its replayed shadow. A checkpoint compares every shadow with its page before the
/// truncation discards the records they were built from.
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
    /// The storage wrote the page outside the journal on purpose (a checkpoint anchor page, which
    /// recovery never replays onto): its shadow no longer describes it. The next transaction to
    /// touch it finds it freshly allocated, with LSN zero, and images it.
    /// </summary>
    internal void Forget(long pageId)
    {
        lock (_sync)
        {
            _shadows.Remove(pageId);
        }
    }

    /// <summary>
    /// A checkpoint is about to truncate the journal: every shadow is compared with its page, then
    /// all are dropped, since every page is imaged again after the checkpoint.
    /// </summary>
    /// <param name="readPage">Reads a page as the storage holds it now: from the buffer pool, or the data file.</param>
    internal void Checkpointing(Action<long, byte[]> readPage)
    {
        KeyValuePair<long, byte[]>[] shadows;
        lock (_sync)
        {
            shadows = [.. _shadows];
            _shadows.Clear();
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

    private void Compare(long pageId, ReadOnlySpan<byte> shadow, ReadOnlySpan<byte> page, bool compareLsn, string what)
    {
        lock (_sync)
        {
            _checks++;
        }

        int difference = PageImageCodec.FirstDifference(shadow, page);
        if (difference >= 0)
        {
            throw Inconsistent(pageId, $"it {what}: the first differing byte is at offset {difference} (0x{page[difference]:X2}, recovery rebuilds 0x{shadow[difference]:X2})");
        }

        long shadowLsn = BinaryPrimitives.ReadInt64LittleEndian(shadow[Page.LsnFieldOffset..]);
        long pageLsn = BinaryPrimitives.ReadInt64LittleEndian(page[Page.LsnFieldOffset..]);
        if (compareLsn && shadowLsn != pageLsn)
        {
            throw Inconsistent(pageId, $"it carries LSN {pageLsn}, but recovery rebuilds it at LSN {shadowLsn}, which its next delta must name");
        }
    }

    private static InvalidOperationException Inconsistent(long pageId, string detail)
        => new($"Storage consistency check failed for page {pageId}: {detail}.");
}
