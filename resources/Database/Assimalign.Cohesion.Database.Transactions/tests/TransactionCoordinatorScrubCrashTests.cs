using System;
using System.Buffers.Binary;
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
/// The open-time scrub after storage format 3's ordered redo (#1253). The storage recovers with
/// its checkpoint deferred and writes every page it rebuilt with the LSN of the last record it
/// applied; the scrub then deletes the unproven writers' versions through ordinary storage
/// brackets, whose deltas name those LSNs, before <c>CompleteRecovery</c> checkpoints. Power is
/// lost at every write the scrub makes — journal drains and stolen data pages, whole and torn —
/// and a second open must recover the first one's journal plus the scrub's records and scrub again
/// to the same state.
/// </summary>
public sealed class TransactionCoordinatorScrubCrashTests
{
    private const int WriterCount = 150;

    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - Recovery scrub: its deltas chain onto the LSNs recovery stamped, with no new page image")]
    public async Task AnalyzeAndScrub_AfterRedo_ShouldChainDeltasOntoRecoveredLsns()
    {
        // Arrange
        var crashed = await CrashedAsync();
        var originalRecords = new StorageJournal(new MemoryStream(crashed.Journal)).ReadAll();
        long originalEnd = originalRecords[^1].Lsn;
        var lastRecordOfPage = originalRecords
            .Where(record => record.Type is JournalRecordType.FullPageImage or JournalRecordType.PageDelta or JournalRecordType.CommittedPageImage)
            .GroupBy(record => (long)record.PageId)
            .ToDictionary(group => group.Key, group => group.Last().Lsn);

        // Act
        using var reopened = CrashStorage.Open(crashed, point: null, poolCapacity: 64);
        await using var recovered = new TransactionCoordinator(reopened, reopened.Log, reopened);
        var plan = recovered.AnalyzeAndScrub();
        var scrubRecords = reopened.Log.ReadAll().Where(record => record.Lsn > originalEnd).ToArray();

        // Assert: the scrub changed pages recovery rebuilt, journaling deltas (or committed images)
        // whose bases are the LSNs recovery stamped, and no full page image.
        plan.Aborted.Count.ShouldBeGreaterThanOrEqualTo(WriterCount);
        var changes = scrubRecords.Where(record => record.Type is JournalRecordType.PageDelta or JournalRecordType.CommittedPageImage).ToArray();
        changes.ShouldNotBeEmpty();
        scrubRecords.ShouldNotContain(record => record.Type == JournalRecordType.FullPageImage);
        foreach (var group in changes.GroupBy(record => (long)record.PageId))
        {
            BinaryPrimitives.ReadInt64LittleEndian(group.First().Payload.Span).ShouldBe(lastRecordOfPage[group.Key], $"page {group.Key}");
        }

        recovered.CompleteRecovery();
        RecordCount(reopened).ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - Recovery scrub: a crash at every write of the scrub recovers and scrubs again to the same state")]
    public async Task AnalyzeAndScrub_CrashAtEveryWrite_ShouldRecoverAndScrubAgain()
    {
        // Arrange: a dry run counts the scrub's writes over a small pool, so it steals pages too.
        var crashed = await CrashedAsync();
        var dryPoint = new CrashPoint();
        using (var dry = CrashStorage.Open(crashed, dryPoint, poolCapacity: 3))
        {
            int afterOpen = dryPoint.Writes;
            await using var coordinator = new TransactionCoordinator(dry, dry.Log, dry);
            coordinator.AnalyzeAndScrub();
            ScrubWrites = dryPoint.Writes - afterOpen;
        }

        ScrubWrites.ShouldBeGreaterThan(2);
        dryPoint.Log.ShouldContain(entry => entry.StartsWith("data Write", StringComparison.Ordinal));

        for (int write = 1; write <= ScrubWrites; write++)
        {
            foreach (int sectors in new[] { 0, 3 })
            {
                // Act: lose power at that write of the scrub, then open, scrub and checkpoint again.
                var point = new CrashPoint { DurableSectors = sectors };
                var first = CrashStorage.Open(crashed, point, poolCapacity: 3); // abandoned after its power loss
                point.CrashAtWrite = point.Writes + write;
                var coordinator = new TransactionCoordinator(first, first.Log, first);
                SimulatedPowerLossException.ShouldBeThrownBy(() => coordinator.AnalyzeAndScrub(), $"scrub write {write}");
                string at = $"scrub write {write} ({point.Log[point.CrashAtWrite - 1]}), {sectors} sectors";

                using var second = CrashStorage.Open(first.CaptureDurable(), point: null, poolCapacity: 3);
                await using var recovered = new TransactionCoordinator(second, second.Log, second);
                var plan = recovered.AnalyzeAndScrub();
                recovered.CompleteRecovery();
                var reader = await recovered.BeginAsync(IsolationLevel.Snapshot);

                // Assert
                Writers.ShouldAllBe(writer => plan.Aborted.Contains(writer), at);
                RecordCount(second).ShouldBe(1, at);
                (await recovered.VersionStore.GetVisibleVersionAsync(0, Committed, reader.Snapshot)).ShouldNotBeNull(at);
                await recovered.CommitAsync(reader);
            }
        }
    }

    private int ScrubWrites { get; set; }

    private List<TransactionSequence> Writers { get; } = [];

    private ulong Committed { get; set; }

    /// <summary>
    /// One committed version and <see cref="WriterCount"/> writers whose versions were stolen to
    /// the data file before power was lost, over a three-page pool.
    /// </summary>
    private async Task<(byte[] Data, byte[] Journal)> CrashedAsync()
    {
        var storage = CrashStorage.Create(poolCapacity: 3); // abandoned: a crash
        var coordinator = new TransactionCoordinator(storage, storage.Log, storage);

        var committed = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        Committed = await InsertAsync(coordinator, storage, committed);
        await coordinator.CommitAsync(committed);
        storage.Checkpoint();

        Writers.Clear();
        for (int i = 0; i < WriterCount; i++)
        {
            var writer = await coordinator.BeginAsync(IsolationLevel.Snapshot);
            await InsertAsync(coordinator, storage, writer);
            Writers.Add(writer.Sequence);
        }

        storage.PageManager.FlushAll();
        return storage.CaptureDurable();
    }

    private static ValueTask<ulong> InsertAsync(TransactionCoordinator coordinator, CrashStorage storage, ITransactionContext context)
        => coordinator.ApplyStatementAsync(context, bracket =>
        {
            byte[] record = new byte[RecordVersionStamp.HeaderSize + 1500];
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

    /// <summary>
    /// A record space over write-through crash-simulation streams, which may share a crash point.
    /// </summary>
    private sealed class CrashStorage : Storage.Storage, ITransactionRecordSpace
    {
        private readonly CrashSimulationStream _data;
        private readonly CrashSimulationStream _journal;

        private CrashStorage(CrashSimulationStream data, CrashSimulationStream journal, int poolCapacity, bool reopen)
            : base(StorageModel.KeyValue, new StorageStream(data), new StorageStream(journal), new StorageStream(new MemoryStream()), poolCapacity)
        {
            _data = data;
            _journal = journal;
            if (reopen)
            {
                OpenExisting(checkpointOnOpen: false);
            }
            else
            {
                InitializeNew((Name)"scrub-crash");
            }
        }


        internal StorageJournal Log => WriteAheadLog;

        internal static CrashStorage Create(int poolCapacity) => new(
            new CrashSimulationStream(writeThrough: true, name: "data"),
            new CrashSimulationStream(writeThrough: true, name: "journal"),
            poolCapacity,
            reopen: false);

        internal static CrashStorage Open((byte[] Data, byte[] Journal) images, CrashPoint? point, int poolCapacity) => new(
            new CrashSimulationStream(images.Data, writeThrough: true, point, "data"),
            new CrashSimulationStream(images.Journal, writeThrough: true, point, "journal"),
            poolCapacity,
            reopen: true);

        internal (byte[] Data, byte[] Journal) CaptureDurable() => (_data.CaptureDurable(), _journal.CaptureDurable());

        internal (PageId PageId, int SlotIndex) Insert(StorageTransaction bracket, ReadOnlySpan<byte> data)
            => InsertRecord(bracket, data);

        public ReadOnlyMemory<byte> Read(PageId pageId, int slotIndex) => ReadRecord(pageId, slotIndex);

        public void Update(StorageTransaction bracket, PageId pageId, int slotIndex, ReadOnlySpan<byte> record)
            => UpdateRecord(bracket, pageId, slotIndex, record);

        public void Delete(StorageTransaction bracket, PageId pageId, int slotIndex)
            => DeleteRecord(bracket, pageId, slotIndex);

        public ulong PackLocation(PageId pageId, int slotIndex)
            => ((ulong)(long)pageId << 16) | (ushort)slotIndex;

        public (PageId PageId, int SlotIndex) UnpackLocation(ulong location)
            => ((PageId)(long)(location >> 16), (int)(location & 0xFFFF));
    }
}
