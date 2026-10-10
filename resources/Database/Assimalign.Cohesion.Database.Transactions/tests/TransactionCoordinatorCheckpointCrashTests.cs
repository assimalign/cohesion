using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Transactions.Tests;

/// <summary>
/// #1242's acceptance at the transaction layer: power is lost at every write a coordinator
/// checkpoint issues — data pages, the header slot, the journal truncation, the checkpoint
/// record — some writes torn, with logical writers and a reader in flight. After every crash the
/// recovered coordinator classifies each writer as aborted and scrubs its version, the committed
/// version stays visible, and LSNs keep increasing.
/// </summary>
public sealed class TransactionCoordinatorCheckpointCrashTests
{
    private const int WriterCount = 40;

    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - Coordinator checkpoint: a crash at every write of a checkpoint keeps in-flight writers' versions invisible and LSNs increasing")]
    public async Task Checkpoint_CrashAtEveryWrite_ShouldKeepUncommittedVersionsInvisible()
    {
        // A dry run counts the checkpoint's writes.
        var dryPoint = new CrashPoint();
        var dry = await ArrangeAsync(dryPoint);
        int before = dryPoint.Writes;
        dry.Coordinator.Checkpoint();
        int checkpointWrites = dryPoint.Writes - before;
        dryPoint.Log.Skip(before).ShouldContain(entry => entry.StartsWith("journal SetLength", StringComparison.Ordinal));

        for (int write = 1; write <= checkpointWrites; write++)
        {
            foreach (int sectors in new[] { 0, 3 })
            {
                var point = new CrashPoint();
                var scenario = await ArrangeAsync(point); // abandoned after its simulated power loss
                point.CrashAtWrite = point.Writes + write;
                point.DurableSectors = sectors;
                SimulatedPowerLossException.ShouldBeThrownBy(() => scenario.Coordinator.Checkpoint(), $"write {write}, {sectors} sectors");
                string at = $"crash at checkpoint write {write} ({point.Log[point.CrashAtWrite - 1]}), {sectors} sectors";

                using var reopened = CrashStorage.Open(scenario.Storage.CaptureDurable());
                await using var recovered = new TransactionCoordinator(reopened, reopened.Log, reopened.Records);
                var plan = recovered.AnalyzeAndScrub();
                recovered.CompleteRecovery();
                var reader = await recovered.BeginAsync(IsolationLevel.Snapshot);

                scenario.Writers.ShouldAllBe(writer => plan.Aborted.Contains(writer), at);
                plan.Committed.ShouldNotContain(scenario.Writers[0], at);
                RecordCount(reopened).ShouldBe(1, at);
                (await recovered.VersionStore.GetVisibleVersionAsync(0, scenario.Committed, reader.Snapshot)).ShouldNotBeNull(at);
                reopened.Log.LastLsn.ShouldBeGreaterThan(scenario.LastLsn, at);
                await recovered.CommitAsync(reader);
            }
        }
    }

    /// <summary>
    /// Since #1252 the writers' records wait in the journal's append buffer until the checkpoint
    /// drains them in the write that leads it. A crash in that write loses records nothing on the
    /// data file depends on: no page the writers changed was written yet (the write-ahead gate
    /// drains the journal through a page's LSN before the page), so recovery finds the committed
    /// version and no trace of the writers, which classify as never begun.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - Coordinator checkpoint: a crash in the drain that leads a checkpoint loses only records no data page depends on")]
    public async Task Checkpoint_CrashInTheLeadingDrain_ShouldLoseNothingTheDataFileDependsOn()
    {
        foreach (int sectors in new[] { 0, 3 })
        {
            // Arrange: the writers' records are still buffered.
            var point = new CrashPoint();
            var scenario = await ArrangeAsync(point, drain: false);
            int before = point.Writes;
            point.CrashAtWrite = before + 1;
            point.DurableSectors = sectors;

            // Act
            SimulatedPowerLossException.ShouldBeThrownBy(() => scenario.Coordinator.Checkpoint(), $"leading drain, {sectors} sectors");
            string at = $"crash at {point.Log[point.CrashAtWrite - 1]}, {sectors} sectors";
            using var reopened = CrashStorage.Open(scenario.Storage.CaptureDurable());
            await using var recovered = new TransactionCoordinator(reopened, reopened.Log, reopened.Records);
            var plan = recovered.AnalyzeAndScrub();
            recovered.CompleteRecovery();
            var reader = await recovered.BeginAsync(IsolationLevel.Snapshot);

            // Assert: the crash hit the journal's drain, before any data page; no writer committed,
            // and only the committed version is there.
            point.Log[point.CrashAtWrite - 1].ShouldStartWith("journal Write", Case.Sensitive, at);
            scenario.Writers.ShouldAllBe(writer => !plan.Committed.Contains(writer), at);
            RecordCount(reopened).ShouldBe(1, at);
            (await recovered.VersionStore.GetVisibleVersionAsync(0, scenario.Committed, reader.Snapshot)).ShouldNotBeNull(at);
            await recovered.CommitAsync(reader);
        }
    }

    /// <summary>
    /// One committed version, <see cref="WriterCount"/> writers that each inserted a version and
    /// are still in flight, and a reader, over write-through crash-simulation streams. Unless
    /// <paramref name="drain"/> is false the journal's append buffer is drained last, so the
    /// writers' records are on the media before the checkpoint, as any commit or stolen page leaves
    /// them, and every crash point is one of the checkpoint's own writes.
    /// </summary>
    private static async Task<Scenario> ArrangeAsync(CrashPoint point, bool drain = true)
    {
        var storage = CrashStorage.Create(point);
        var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records);

        var committed = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        var location = await InsertAsync(coordinator, storage, committed);
        await coordinator.CommitAsync(committed);

        var writers = new List<TransactionSequence>();
        for (int i = 0; i < WriterCount; i++)
        {
            var writer = await coordinator.BeginAsync(IsolationLevel.Snapshot);
            await InsertAsync(coordinator, storage, writer);
            writers.Add(writer.Sequence);
        }

        _ = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        if (drain)
        {
            storage.Log.Flush();
        }

        return new Scenario(storage, coordinator, writers, location, storage.Log.LastLsn);
    }

    private static ValueTask<ulong> InsertAsync(TransactionCoordinator coordinator, CrashStorage storage, TransactionContext context)
        => coordinator.ApplyStatementAsync(context, bracket =>
        {
            byte[] record = new byte[RecordVersionStamp.HeaderSize + 1];
            RecordVersionStamp.WriteWriter(record, context.Sequence);
            record[RecordVersionStamp.HeaderSize] = 42;
            var (pageId, slotIndex) = storage.Insert(bracket, record);
            coordinator.VersionStore.RecordCreated(context.Sequence, pageId, slotIndex);
            return storage.PackLocation(pageId, slotIndex);
        });

    private static int RecordCount(Storage.Storage storage)
    {
        using var iterator = storage.GetUnitIterator();
        int count = 0;
        while (iterator.MoveNext())
        {
            count++;
        }

        return count;
    }

    private sealed record Scenario(
        CrashStorage Storage,
        TransactionCoordinator Coordinator,
        List<TransactionSequence> Writers,
        ulong Committed,
        long LastLsn);

    /// <summary>
    /// A record space over write-through crash-simulation streams sharing one crash point.
    /// </summary>
    private sealed class CrashStorage : Storage.Storage
    {
        private readonly CrashSimulationStream _data;
        private readonly CrashSimulationStream _journal;

        private CrashStorage(CrashSimulationStream data, CrashSimulationStream journal, bool reopen)
            : base(StorageModel.KeyValue, new StorageStream(data), new StorageStream(journal), new StorageStream(new MemoryStream()))
        {
            _data = data;
            _journal = journal;
            if (reopen)
            {
                OpenExisting(checkpointOnOpen: false);
            }
            else
            {
                InitializeNew((Name)"checkpoint-crash");
            }
        }

        internal StorageJournal Log => WriteAheadLog;

        internal static CrashStorage Create(CrashPoint point) => new(
            new CrashSimulationStream(writeThrough: true, point, "data"),
            new CrashSimulationStream(writeThrough: true, point, "journal"),
            reopen: false);

        internal static CrashStorage Open((byte[] Data, byte[] Journal) images) => new(
            new CrashSimulationStream(images.Data, writeThrough: true),
            new CrashSimulationStream(images.Journal, writeThrough: true),
            reopen: true);

        internal (byte[] Data, byte[] Journal) CaptureDurable() => (_data.CaptureDurable(), _journal.CaptureDurable());

        internal (PageId PageId, int SlotIndex) Insert(StorageTransaction bracket, ReadOnlySpan<byte> data)
            => InsertRecord(bracket, data);

        internal ReadOnlyMemory<byte> Read(PageId pageId, int slotIndex) => ReadRecord(pageId, slotIndex);

        internal void Update(StorageTransaction bracket, PageId pageId, int slotIndex, ReadOnlySpan<byte> record)
            => UpdateRecord(bracket, pageId, slotIndex, record);

        internal void Delete(StorageTransaction bracket, PageId pageId, int slotIndex)
            => DeleteRecord(bracket, pageId, slotIndex);

        internal ulong PackLocation(PageId pageId, int slotIndex)
            => ((ulong)(long)pageId << 16) | (ushort)slotIndex;

        internal (PageId PageId, int SlotIndex) UnpackLocation(ulong location)
            => ((PageId)(long)(location >> 16), (int)(location & 0xFFFF));

        /// <summary>
        /// Gets the coordinator's record space over this storage's records. The double used to be
        /// the record space itself; both are abstract classes now, so it is split (plan C9).
        /// </summary>
        internal TransactionRecordSpace Records => _records ??= new RecordSpace(this);

        private RecordSpace? _records;

        private sealed class RecordSpace : TransactionRecordSpace
        {
            private readonly CrashStorage _storage;

            internal RecordSpace(CrashStorage storage)
            {
                _storage = storage;
            }

            protected override ReadOnlyMemory<byte> ReadCore(PageId pageId, int slotIndex) => _storage.Read(pageId, slotIndex);

            protected override void UpdateCore(StorageTransaction transaction, PageId pageId, int slotIndex, ReadOnlySpan<byte> record)
                => _storage.Update(transaction, pageId, slotIndex, record);

            protected override void DeleteCore(StorageTransaction transaction, PageId pageId, int slotIndex)
                => _storage.Delete(transaction, pageId, slotIndex);

            protected override ulong PackLocationCore(PageId pageId, int slotIndex) => _storage.PackLocation(pageId, slotIndex);

            protected override (PageId PageId, int SlotIndex) UnpackLocationCore(ulong location) => _storage.UnpackLocation(location);
        }
    }
}
