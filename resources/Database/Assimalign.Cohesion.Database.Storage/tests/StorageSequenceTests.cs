using System;
using System.Text;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Storage.Tests;

/// <summary>
/// Tests for the storage transaction-sequence space: external reservation and
/// adopted brackets (the seam an MVCC manager shares the storage's sequence
/// namespace through), and the header floor that keeps sequences monotonic
/// across a checkpoint-then-reopen (row version stamps persist in data pages, so
/// a recycled sequence would corrupt snapshot visibility).
/// </summary>
public sealed class StorageSequenceTests
{
    private sealed class SequenceStorage : Storage
    {
        private SequenceStorage(StorageStream data, StorageStream journal)
            : base(data, journal, new StorageStream(new System.IO.MemoryStream())) { }

        public override StorageModel Model => StorageModel.Custom;

        public static SequenceStorage Create(CrashSimulationStream data, CrashSimulationStream journal)
        {
            var storage = new SequenceStorage(new StorageStream(data), new StorageStream(journal));
            storage.InitializeNew((Name)"sequence-harness");
            return storage;
        }

        public static SequenceStorage Open(byte[] data, byte[] journal)
        {
            var storage = new SequenceStorage(
                new StorageStream(new CrashSimulationStream(data, writeThrough: true)),
                new StorageStream(new CrashSimulationStream(journal)));
            storage.OpenExisting();
            return storage;
        }

        public (PageId PageId, int SlotIndex) Insert(IStorageTransaction transaction, byte[] data)
            => InsertRecord(transaction, data);
    }

    private static byte[] Text(string value) => Encoding.UTF8.GetBytes(value);

    [Fact(DisplayName = "Cohesion Test [Storage] - Sequences: Reservation and adoption share one monotonic space with internal brackets")]
    public void ReserveTransactionSequence_MixedWithInternalBrackets_ShouldStayMonotonic()
    {
        // Arrange
        var data = new CrashSimulationStream(writeThrough: true);
        var journal = new CrashSimulationStream();
        using var storage = SequenceStorage.Create(data, journal);

        // Act: an internally sequenced bracket, an external reservation, and an
        // adopted bracket interleave.
        using var internalBracket = storage.BeginTransaction();
        long reserved = storage.ReserveTransactionSequence();
        using var adopted = storage.BeginTransaction(reserved);
        using var laterBracket = storage.BeginTransaction();

        // Assert: one strictly increasing namespace, and the adopted bracket
        // carries exactly the reserved sequence.
        reserved.ShouldBeGreaterThan(internalBracket.Sequence);
        adopted.Sequence.ShouldBe(reserved);
        laterBracket.Sequence.ShouldBeGreaterThan(reserved);

        internalBracket.Commit();
        adopted.Commit();
        laterBracket.Commit();
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Sequences: An adopted bracket commits durably under its shared sequence")]
    public void BeginTransaction_AdoptedSequence_ShouldCommitUnderSharedSequence()
    {
        // Arrange
        var data = new CrashSimulationStream(writeThrough: true);
        var journal = new CrashSimulationStream();
        using var storage = SequenceStorage.Create(data, journal);

        long reserved = storage.ReserveTransactionSequence();

        // Act: write through the adopted bracket and commit.
        using (var adopted = storage.BeginTransaction(reserved))
        {
            storage.Insert(adopted, Text("adopted-row"));
            adopted.Commit();
        }

        // Assert: reopening from the durable images recovers the committed row —
        // the adopted bracket's page images and commit record prove the sequence.
        using var reopened = SequenceStorage.Open(data.CaptureDurable(), journal.CaptureDurable());
        using var iterator = reopened.GetUnitIterator();
        iterator.MoveNext().ShouldBeTrue();
        Encoding.UTF8.GetString(iterator.Current.Data.Span).ShouldBe("adopted-row");
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Sequences: The header floor keeps sequences monotonic across checkpoint and reopen")]
    public void OpenExisting_AfterCheckpointTruncation_ShouldResumeSequencesAboveFloor()
    {
        // Arrange: consume sequences, then checkpoint — the journal (the only
        // other sequence witness) is truncated, so the header floor is what
        // carries the high-water mark across the reopen.
        var data = new CrashSimulationStream(writeThrough: true);
        var journal = new CrashSimulationStream();
        long highWater;

        using (var storage = SequenceStorage.Create(data, journal))
        {
            using (var bracket = storage.BeginTransaction())
            {
                storage.Insert(bracket, Text("row"));
                bracket.Commit();
            }

            highWater = storage.ReserveTransactionSequence();
            storage.Checkpoint();
        }

        // Act
        using var reopened = SequenceStorage.Open(data.CaptureDurable(), journal.CaptureDurable());
        long next = reopened.ReserveTransactionSequence();

        // Assert: assignment resumed above the pre-checkpoint high-water mark.
        next.ShouldBeGreaterThan(highWater);
    }
}
