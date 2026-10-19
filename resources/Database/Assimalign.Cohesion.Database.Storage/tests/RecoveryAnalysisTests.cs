using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Storage.Internal;
using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;
using Assimalign.Cohesion.Database.Storage.Units;

namespace Assimalign.Cohesion.Database.Storage.Tests;

[Collection(StorageRaceCollection.Name)]
public sealed class RecoveryAnalysisTests
{
    [Fact(DisplayName = "Cohesion Test [Storage] - Recovery fusion: 1,000 fixed seeds preserve base plans, redo order and recovered bytes")]
    public void Run_SeededInterleavedJournal_ShouldMatchBaseTwoPassPlannerAndRedo()
    {
        for (int seed = 0; seed < 1_000; seed++)
        {
            // Arrange: shared pages, pre/post images, deltas, rollback/reuse, checkpoints,
            // uncommitted FPI after committed delta, and duplicate/decreasing verified LSNs.
            byte[] bytes = RecoveryJournalScenario.Create(seed);
            var protectedPages = new HashSet<long> { 2, seed % 3 + 4 };
            using var baseMedium = new RecoveryRecordingStream();
            using var fusedMedium = new RecoveryRecordingStream();
            long originalLength = (seed % 9 + 1) * Page.Size;
            baseMedium.SetLength(originalLength);
            fusedMedium.SetLength(originalLength);
            using var baseData = new StorageStream(baseMedium);
            using var fusedData = new StorageStream(fusedMedium);
            using var baseJournal = StorageJournal.Create(new MemoryStream(bytes));
            using var fusedJournal = StorageJournal.Create(new MemoryStream(bytes));

            // Act: the oracle's first two passes were copied verbatim from 179db17d1.
            var expected = BaseStorageRecovery.Plan(baseData, baseJournal, protectedPages);
            var analysis = fusedJournal.ReadRecoveryAnalysis(CancellationToken.None, out _);
            var actual = analysis.Pages.Where(entry => !protectedPages.Contains(entry.Key)
                && (entry.Value.Committed || (entry.Key + 1) * Page.Size <= originalLength))
                .Select(entry => (entry.Key, entry.Value.LastLsn, entry.Value.Committed)).OrderBy(entry => entry.Key);

            // Assert: classification, last applicable record, committed-page flag and exclusions.
            analysis.MaxSequence.ShouldBe(expected.MaxSequence, $"seed {seed}");
            analysis.CheckpointLsn.ShouldBe(expected.CheckpointLsn, $"seed {seed}");
            analysis.Committed.Order().ShouldBe(expected.Committed.Order(), $"seed {seed}");
            actual.ShouldBe(expected.Pages.Select(entry => (entry.Key, entry.Value.LastLsn, entry.Value.Committed))
                .OrderBy(entry => entry.Key), $"seed {seed}");

            // Act: compare the original redo as well, including its bounded cache retirements.
            int capacity = seed % 4 + 1;
            var baseResult = BaseStorageRecovery.Run(baseData, baseJournal, false, protectedPages, capacity);
            var fusedResult = StorageRecovery.Run(fusedData, fusedJournal, false, protectedPages, capacity);

            // Assert: intermediate page writes in order, their complete checksums, and final bytes.
            fusedResult.MaxSequence.ShouldBe(baseResult.MaxSequence, $"seed {seed}");
            fusedResult.CheckpointLsn.ShouldBe(baseResult.CheckpointLsn, $"seed {seed}");
            fusedResult.RebuiltPages.Order().ShouldBe(baseResult.RebuiltPages.Order(), $"seed {seed}");
            fusedMedium.Writes.ShouldBe(baseMedium.Writes, $"seed {seed}");
            fusedMedium.ToArray().ShouldBe(baseMedium.ToArray(), $"seed {seed}");
        }
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Recovery analysis: thousands of rolled-back writers recycle bounded pending page dictionaries")]
    public void ReadRecoveryAnalysis_ManyRolledBackWriters_ShouldRecyclePendingPages()
    {
        // Arrange: 131,072 distinct pending pages, none requiring a surviving page plan.
        using var medium = new MemoryStream();
        using (var writer = StorageJournal.Create(medium, leaveOpen: true))
        {
            for (int sequence = 1; sequence <= 4_096; sequence++)
            {
                for (int page = 1; page <= 32; page++)
                {
                    writer.AppendPageRecord(sequence, (PageId)(long)(sequence * 32 + page), JournalRecordType.PageDelta, [], out _);
                }
                writer.AppendRollback(sequence);
            }
            writer.Flush();
        }
        byte[] bytes = medium.ToArray();
        long allocated = long.MaxValue;
        StorageRecoveryAnalysis? analysis = null;

        // Act: warm the reader/pool, then keep the minimum to exclude GC context retirement.
        for (int round = 0; round < 4; round++)
        {
            using var journal = StorageJournal.Create(new MemoryStream(bytes));
            long before = GC.GetAllocatedBytesForCurrentThread();
            analysis = journal.ReadRecoveryAnalysis(CancellationToken.None, out _);
            if (round > 0)
            {
                allocated = Math.Min(allocated, GC.GetAllocatedBytesForCurrentThread() - before);
            }
        }

        // Assert: only rollback sequence identities and one reusable 32-page dictionary are needed.
        // Retaining pending pages, or allocating a new dictionary per writer, costs many MiB.
        analysis!.MaxSequence.ShouldBe(4_096);
        analysis.Pages.ShouldBeEmpty();
        analysis.Committed.ShouldBeEmpty();
        allocated.ShouldBeLessThan(1_048_576, $"{allocated} bytes for 4,096 rolled-back writers");
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Recovery analysis: consuming the cached plan releases it and the next analysis verifies CRCs")]
    public void ReadRecoveryAnalysis_ConsumedPlan_ShouldRescanOnNextUse()
    {
        // Arrange
        using var medium = new MemoryStream();
        using (var writer = StorageJournal.Create(medium, leaveOpen: true))
        {
            writer.AppendPageRecord(1, (PageId)1L, JournalRecordType.FullPageImage, [], out _);
            writer.AppendCommit(1);
            writer.Flush();
        }
        using var journal = StorageJournal.Create(medium, leaveOpen: true);
        journal.LastLsn.ShouldBe(2);
        var first = journal.ReadRecoveryAnalysis(CancellationToken.None, out _);
        medium.GetBuffer()[37] ^= 0xFF;

        // Act
        var next = journal.ReadRecoveryAnalysis(CancellationToken.None, out _);

        // Assert
        first.Pages.ShouldHaveSingleItem();
        next.ShouldNotBeSameAs(first);
        next.Pages.ShouldBeEmpty();
        next.Committed.ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Storage] - Journal readers: later scans always verify previously read frames")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ReadSequential_PreviouslyVerifiedFrameDamaged_ShouldStopAtCorruption(int reader)
    {
        // Arrange
        using var medium = new MemoryStream();
        using var journal = StorageJournal.Create(medium, leaveOpen: true);
        journal.AppendCommit(1);
        journal.AppendCommit(2);
        journal.ReadSequentialBorrowed().Count().ShouldBe(2);
        medium.GetBuffer()[(int)medium.Length - 1] ^= 0xFF;

        // Act
        var records = reader switch
        {
            0 => journal.ReadSequential().ToArray(),
            1 => journal.ReadSequentialBorrowed().ToArray(),
            _ => journal.ReadAll().ToArray(),
        };

        // Assert
        records.ShouldHaveSingleItem().TransactionSequence.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Recovery CRC trust: only one immediate replay on the same instance reuses the prefix")]
    public void ReadRecoveryRecords_SingleUseToken_ShouldVerifyEveryOtherRead()
    {
        // Arrange: corrupt only the stored CRC after analysis to observe exactly which read trusts.
        using var medium = new MemoryStream();
        using var journal = StorageJournal.Create(medium, leaveOpen: true);
        journal.AppendCommit(1);
        journal.ReadRecoveryAnalysis(CancellationToken.None, out var token);
        medium.GetBuffer()[(int)medium.Length - 1] ^= 0xFF;
        using var other = StorageJournal.Create(new MemoryStream(medium.ToArray()));

        // Act / Assert
        other.ReadRecoveryRecords(token).ShouldBeEmpty();
        journal.ReadRecoveryRecords(token).Count().ShouldBe(1);
        journal.ReadRecoveryRecords(token).ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Storage] - Recovery CRC trust: an intervening sequential or point read revokes replay trust")]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadRecoveryRecords_InterveningRead_ShouldInvalidateToken(bool pointRead)
    {
        // Arrange
        using var medium = new MemoryStream();
        using var journal = StorageJournal.Create(medium, leaveOpen: true);
        journal.AppendPageRecord(1, (PageId)1L, JournalRecordType.FullPageImage, [], out var location);
        journal.AppendCommit(1);
        journal.ReadRecoveryAnalysis(CancellationToken.None, out var token);
        if (pointRead)
        {
            journal.ReadPageRecord(location, new byte[PageImageCodec.MaximumPayloadLength]).ShouldBe(0);
        }
        else
        {
            journal.ReadAll().Count.ShouldBe(2);
        }
        medium.GetBuffer()[37] ^= 0xFF;

        // Act / Assert
        journal.ReadRecoveryRecords(token).ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Storage] - Journal truncation: a failing provider invalidates cached plans and replay trust before changing bytes")]
    [InlineData(false)]
    [InlineData(true)]
    public void Checkpoint_TruncationChangesBytesThenFails_ShouldDiscardAnalysisAndTrust(bool consumeAnalysis)
    {
        // Arrange: initialize an existing journal, leaving its cached plan unconsumed or
        // preparing the single-use replay token. Neither writes nor reads intervene.
        using var medium = new RecoveryTruncationStream();
        using (var writer = StorageJournal.Create(medium, leaveOpen: true))
        {
            writer.AppendCommit(1);
            writer.Flush();
        }
        using var journal = StorageJournal.Create(medium, leaveOpen: true);
        journal.LastLsn.ShouldBe(1);
        object token = new();
        if (consumeAnalysis)
        {
            journal.ReadRecoveryAnalysis(CancellationToken.None, out token);
        }
        medium.FailTruncation = true;

        // Act
        var failure = Should.Throw<StorageOfflineException>(() => journal.Checkpoint([]));

        // Assert: the offline reader must observe the damage, even at unchanged offsets/length.
        failure.InnerException.ShouldBeOfType<IOException>();
        if (consumeAnalysis)
        {
            journal.ReadRecoveryRecords(token).ShouldBeEmpty();
        }
        else
        {
            journal.ReadRecoveryAnalysis(CancellationToken.None, out _).Committed.ShouldBeEmpty();
        }
    }
}
