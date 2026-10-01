using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Storage.Internal;
using Assimalign.Cohesion.Database.Storage.Units;

namespace Assimalign.Cohesion.Database.Storage.Tests;

/// <summary>
/// Storage transactions committing from several threads at once against a buffer pool
/// far smaller than the working set (#1157). Each thread owns one record chain and runs
/// insert, grow, shrink and delete transactions on it — growing records exercises
/// slotted-page relocation, emptied pages are released and reallocated across chains —
/// while readers scan committed chains, and a page writer and checkpointer run in the
/// background. After every phase each chain is compared with its model, every page on
/// the data stream must carry a valid checksum, and a reopen through recovery must read
/// back the same records.
/// </summary>
public sealed class StorageConcurrencyTests
{
    private const int writerCount = 4;
    private const int readerCount = 2;
    private const int poolCapacity = 8;
    private const int phaseCount = 3;
    private const int transactionsPerPhase = 150;
    private const ulong firstOwner = 100;

    [Theory(DisplayName = "Cohesion Test [Storage] - Concurrency: concurrent commits on a small pool keep every committed record and page checksum")]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Commit_ConcurrentTransactionsOnSmallPool_ShouldKeepEveryCommittedRecord(int seed)
    {
        // Arrange
        var dataStream = new MemoryStream();
        var journalStream = new MemoryStream();
        using var storage = ConcurrentStorage.Create(dataStream, journalStream, poolCapacity);
        var models = Enumerable.Range(0, writerCount).Select(_ => new Dictionary<(long, int), byte[]>()).ToArray();
        var chainLocks = Enumerable.Range(0, writerCount).Select(_ => new ReaderWriterLockSlim()).ToArray();

        try
        {
            for (int phase = 1; phase <= phaseCount; phase++)
            {
                // Act
                using var stop = new CancellationTokenSource();
                var writers = Enumerable.Range(0, writerCount).Select(writer => Task.Run(() =>
                    RunWriter(storage, writer, models, chainLocks[writer], new Random(HashCode.Combine(seed, phase, writer))))).ToArray();

                var background = new List<Task>();
                for (int reader = 0; reader < readerCount; reader++)
                {
                    var random = new Random(HashCode.Combine(seed, phase, 100 + reader));
                    background.Add(Task.Run(() =>
                    {
                        while (!stop.IsCancellationRequested)
                        {
                            int owner = random.Next(writerCount);
                            chainLocks[owner].EnterReadLock();
                            try
                            {
                                AssertChain(storage, owner, models[owner]);
                            }
                            finally
                            {
                                chainLocks[owner].ExitReadLock();
                            }
                        }
                    }));
                }

                background.Add(Task.Run(() =>
                {
                    int pass = 0;
                    while (!stop.IsCancellationRequested)
                    {
                        storage.WriteBackDirtyPages(4);
                        if (++pass % 16 == 0)
                        {
                            try
                            {
                                storage.Checkpoint();
                            }
                            catch (StorageTransactionException)
                            {
                                // A writer is mid-transaction; the next pass retries.
                            }
                        }
                    }
                }));

                await Task.WhenAll(writers);
                stop.Cancel();
                await Task.WhenAll(background);

                // Assert
                VerifyPhase(storage, dataStream, journalStream, models);
            }
        }
        finally
        {
            foreach (var chainLock in chainLocks)
            {
                chainLock.Dispose();
            }
        }
    }

    private static void RunWriter(
        ConcurrentStorage storage,
        int writer,
        Dictionary<(long, int), byte[]>[] models,
        ReaderWriterLockSlim chainLock,
        Random random)
    {
        ulong owner = firstOwner + (ulong)writer;
        int sequence = 0;

        for (int i = 0; i < transactionsPerPhase; i++)
        {
            chainLock.EnterWriteLock();
            try
            {
                var working = new Dictionary<(long, int), byte[]>(models[writer]);
                using var transaction = storage.BeginTransaction();
                int operations = random.Next(1, 5);

                for (int operation = 0; operation < operations; operation++)
                {
                    int choice = working.Count < 8 ? 0 : random.Next(4);
                    if (choice == 0 || working.Count == 0)
                    {
                        var record = Record(writer, ++sequence, random.Next(8, 900));
                        var location = storage.Insert(transaction, owner, record);
                        working.Add(((long)location.PageId, location.SlotIndex), record);
                    }
                    else
                    {
                        var target = working.Keys.ElementAt(random.Next(working.Count));
                        if (choice == 3)
                        {
                            storage.Delete(transaction, (PageId)target.Item1, target.Item2);
                            working.Remove(target);
                        }
                        else
                        {
                            // Grow or shrink in place; a record that no longer fits its page
                            // moves, the way the engines' catalogs relocate a record.
                            var record = Record(writer, ++sequence, random.Next(8, 1800));
                            try
                            {
                                storage.Update(transaction, (PageId)target.Item1, target.Item2, record);
                                working[target] = record;
                            }
                            catch (SlottedPageException)
                            {
                                storage.Delete(transaction, (PageId)target.Item1, target.Item2);
                                working.Remove(target);
                                var location = storage.Insert(transaction, owner, record);
                                working.Add(((long)location.PageId, location.SlotIndex), record);
                            }
                        }
                    }
                }

                if (random.Next(8) == 0)
                {
                    transaction.Rollback();
                }
                else
                {
                    transaction.Commit();
                    models[writer] = working;
                }
            }
            finally
            {
                chainLock.ExitWriteLock();
            }
        }
    }

    private static void VerifyPhase(
        ConcurrentStorage storage,
        MemoryStream dataStream,
        MemoryStream journalStream,
        Dictionary<(long, int), byte[]>[] models)
    {
        // Through the live storage: every chain matches its model.
        for (int writer = 0; writer < writerCount; writer++)
        {
            AssertChain(storage, writer, models[writer]);
        }

        ((StorageBufferPool)storage.BufferPool).CheckInvariants();

        // On the stream: a checkpoint writes every dirty page, and each one verifies.
        storage.Checkpoint();
        byte[] data = Snapshot(dataStream);
        byte[] journal = Snapshot(journalStream);
        (data.Length % Page.Size).ShouldBe(0);
        for (long pageId = 0; pageId < data.Length / Page.Size; pageId++)
        {
            Should.NotThrow(() => PageChecksum.Verify(data.AsSpan((int)(pageId * Page.Size), Page.Size), (PageId)pageId));
        }

        // Through recovery: the reopened file set reads back the same chains.
        using var reopened = ConcurrentStorage.Open(data, journal, poolCapacity);
        for (int writer = 0; writer < writerCount; writer++)
        {
            AssertChain(reopened, writer, models[writer]);
        }
    }

    private static void AssertChain(ConcurrentStorage storage, int writer, Dictionary<(long, int), byte[]> model)
    {
        var scanned = storage.Scan(firstOwner + (ulong)writer);
        scanned.Count.ShouldBe(model.Count, $"chain {writer} record count");
        foreach (var (location, data) in scanned)
        {
            model.TryGetValue(location, out var expected).ShouldBeTrue($"chain {writer} holds an unexpected record at {location}");
            data.AsSpan().SequenceEqual(expected).ShouldBeTrue($"chain {writer} record at {location}");
        }
    }

    // Called between phases, when nothing writes to the streams.
    private static byte[] Snapshot(MemoryStream stream) => stream.ToArray();

    private static byte[] Record(int writer, int sequence, int length)
    {
        var record = new byte[length];
        BitConverter.TryWriteBytes(record, sequence);
        for (int i = sizeof(int); i < length; i++)
        {
            record[i] = (byte)((writer * 17) + (sequence * 3) + i);
        }

        return record;
    }

    /// <summary>
    /// A concrete storage over simulated durable handles exposing owner-chain record operations.
    /// </summary>
    private sealed class ConcurrentStorage : Storage
    {
        private ConcurrentStorage(StorageStream data, StorageStream journal, int capacity)
            : base(data, journal, new StorageStream(new MemoryStream()), capacity)
        {
        }

        public override StorageModel Model => StorageModel.Custom;

        public static ConcurrentStorage Create(MemoryStream data, MemoryStream journal, int capacity)
        {
            var storage = new ConcurrentStorage(
                new StorageStream(new SimulatedDurableFileHandle(data)),
                new StorageStream(new SimulatedDurableFileHandle(journal)),
                capacity);
            storage.InitializeNew((Name)"concurrency");
            return storage;
        }

        public static ConcurrentStorage Open(byte[] data, byte[] journal, int capacity)
        {
            var storage = new ConcurrentStorage(
                new StorageStream(new SimulatedDurableFileHandle(Writable(data))),
                new StorageStream(new SimulatedDurableFileHandle(Writable(journal))),
                capacity);
            storage.OpenExisting();
            return storage;
        }

        public (PageId PageId, int SlotIndex) Insert(IStorageTransaction transaction, ulong ownerId, byte[] data)
            => InsertRecord(transaction, ownerId, data);

        public void Update(IStorageTransaction transaction, PageId pageId, int slotIndex, byte[] data)
            => UpdateRecord(transaction, pageId, slotIndex, data);

        public void Delete(IStorageTransaction transaction, PageId pageId, int slotIndex)
            => DeleteRecord(transaction, pageId, slotIndex);

        public List<((long, int) Location, byte[] Data)> Scan(ulong ownerId)
        {
            var results = new List<((long, int), byte[])>();
            using var iterator = GetUnitIterator(ownerId);
            while (iterator.MoveNext())
            {
                var unit = iterator.Current;
                results.Add((((long)unit.PageId, unit.SlotIndex), unit.Data.ToArray()));
            }

            return results;
        }

        private static MemoryStream Writable(byte[] content)
        {
            var stream = new MemoryStream();
            stream.Write(content);
            stream.Position = 0;
            return stream;
        }
    }
}
