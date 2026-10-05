using System;
using System.Collections.Concurrent;
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

                // Stop the readers even when a writer fails: disposing a chain lock a reader
                // still holds throws SynchronizationLockException, which would replace the
                // writer's error.
                try
                {
                    await Task.WhenAll(writers);
                }
                finally
                {
                    stop.Cancel();
                    await Task.WhenAll(background);
                }

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

    [Fact(DisplayName = "Cohesion Test [Storage] - Concurrency: a page a commit frees is never handed to another transaction while the commit still write-locks it")]
    public void Commit_FreedPageAllocatedByAnotherTransactionAtOnce_ShouldNotBeWriteLocked()
    {
        // Arrange: a record alone on its page, so deleting it releases the page at commit, and
        // a second transaction already open, the way a concurrent writer is. Nothing else is
        // free, so the page is the next one the allocator hands out.
        using var storage = ConcurrentStorage.Create(new MemoryStream(), new MemoryStream(), poolCapacity);
        PageId freedPage;
        using (var setup = storage.BeginTransaction())
        {
            freedPage = storage.Insert(setup, firstOwner, Record(0, 1, 64)).PageId;
            setup.Commit();
        }

        using var deleting = storage.BeginTransaction();
        storage.Delete(deleting, freedPage, 0);
        using var inserting = storage.BeginTransaction();

        // The other writer's insert runs at the instant the page joins the free list, the
        // window two CI runs hit (#1157 test, seeds 2 and 3): it allocates a page for a new
        // chain, so it takes the freed one (InsertRecord → AllocateDataPage → RegisterTouch).
        // The handler runs on the committing thread, so the insert re-enters whatever storage
        // locks the commit holds at that point and sees the page exactly as the commit left it
        // on the list: it must already be unlocked.
        var freeSpaceMap = (StorageFreeSpaceMap)storage.FreeSpaceMap;
        var inserted = new List<PageId>();
        var failures = new List<Exception>();
        freeSpaceMap.Freed = pageId =>
        {
            if (pageId != freedPage)
            {
                return;
            }

            try
            {
                inserted.Add(storage.Insert(inserting, firstOwner + 1, Record(1, 1, 64)).PageId);
            }
            catch (StorageException exception)
            {
                failures.Add(exception);
            }
        };

        // Act
        try
        {
            deleting.Commit();
        }
        finally
        {
            freeSpaceMap.Freed = null;
        }

        // Assert: the insert got the page, and with it the page's write lock.
        failures.Select(exception => exception.Message).ShouldBeEmpty();
        inserted.ShouldBe([freedPage]);
        inserting.Commit();
        AssertChain(storage, 0, new Dictionary<(long, int), byte[]>());
        AssertChain(storage, 1, new Dictionary<(long, int), byte[]> { [((long)freedPage, 0)] = Record(1, 1, 64) });
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Concurrency: a page one transaction releases twice enters the free list once, so it is never handed out twice")]
    public void Commit_PageReleasedTwiceInOneTransaction_ShouldEnterTheFreeListOnce()
    {
        // Arrange: deleting the last record releases its page, and releasing the whole chain in
        // the same transaction releases it again: the chain lists the page until commit.
        using var storage = ConcurrentStorage.Create(new MemoryStream(), new MemoryStream(), poolCapacity);
        PageId page;
        using (var setup = storage.BeginTransaction())
        {
            page = storage.Insert(setup, firstOwner, Record(0, 1, 64)).PageId;
            setup.Commit();
        }

        using var dropping = storage.BeginTransaction();
        storage.Delete(dropping, page, 0);
        storage.FreeOwnerPages(dropping, firstOwner).ShouldBe(1);

        // Another allocation takes the page each time it joins the free list.
        var freeSpaceMap = (StorageFreeSpaceMap)storage.FreeSpaceMap;
        var handedOut = new List<PageId>();
        freeSpaceMap.Freed = pageId =>
        {
            if (pageId == page)
            {
                handedOut.Add(freeSpaceMap.Allocate());
            }
        };

        // Act
        try
        {
            dropping.Commit();
        }
        finally
        {
            freeSpaceMap.Freed = null;
        }

        // Assert: one hand-out; a second free would have put the allocated page back on the
        // free list, and the next allocation would hand it to a second owner.
        handedOut.ShouldBe([page]);
        freeSpaceMap.IsAllocated(page).ShouldBeTrue();
        freeSpaceMap.FreePageCount.ShouldBe(0);
        freeSpaceMap.Allocate().ShouldNotBe(page);
    }

    [Theory(DisplayName = "Cohesion Test [Storage] - Concurrency: in-place updates beside page allocation and paced write-back keep every committed version on an in-memory stream")]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Update_ConcurrentWithPageAllocationOnInMemoryStream_ShouldKeepEveryCommittedVersion(int seed)
    {
        // Arrange: the production in-memory stream (StorageStream.FromInMemory, what every
        // engine's in-memory strategy uses). Each record takes a page of its own, so the
        // inserters extend the data stream on every commit, across many capacity doublings
        // of its array, while updaters rewrite old records in place and the page writer and
        // the small pool's evictions write those pages back. A write-back that raced an
        // extension used to land in the array being replaced: the pool recorded the page
        // clean, evicted it, and reloaded the older image (#1157 review).
        const int inserterCount = 3;
        const int updaterCount = 3;
        const int targetPages = 2048;
        const int recordSize = 5000;
        using var storage = ConcurrentStorage.CreateInMemory(capacity: 8);
        var locations = new ConcurrentDictionary<(int Inserter, int Row), (PageId PageId, int SlotIndex)>();
        var versions = new ConcurrentDictionary<(int Inserter, int Row), int>();
        var wrong = new ConcurrentQueue<string>();
        var latest = new int[inserterCount];
        int inserting = inserterCount;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // Act
        var inserters = Enumerable.Range(0, inserterCount).Select(inserter => Task.Run(() =>
        {
            var record = new byte[recordSize];
            for (int row = 1; storage.Data.Length < targetPages * (long)Page.Size && !deadline.IsCancellationRequested; row++)
            {
                EncodeVersion(record, inserter, row, 0);
                using var transaction = storage.BeginTransaction();
                var location = storage.Insert(transaction, firstOwner + (ulong)inserter, record);
                transaction.Commit();
                locations[(inserter, row)] = location;
                versions[(inserter, row)] = 0;
                Volatile.Write(ref latest[inserter], row);
            }

            Interlocked.Decrement(ref inserting);
        })).ToArray();

        var updaters = Enumerable.Range(0, updaterCount).Select(updater => Task.Run(() =>
        {
            var random = new Random(HashCode.Combine(seed, updater));
            var record = new byte[recordSize];
            while (Volatile.Read(ref inserting) > 0)
            {
                // Each updater owns a residue class of rows, and stays behind the inserter's
                // newest rows so it never shares a page with an insert in flight.
                int inserter = random.Next(inserterCount);
                int limit = Volatile.Read(ref latest[inserter]) - 4;
                int row = limit < 2 ? 0 : 1 + random.Next(limit - 1);
                if (row == 0 || row % updaterCount != updater || !locations.TryGetValue((inserter, row), out var location))
                {
                    Thread.Yield();
                    continue;
                }

                // Read before rewriting: a reload of a page whose write-back was lost shows
                // the older version here, before this update would paper over it.
                int committed = versions[(inserter, row)];
                Check(storage, (inserter, row), location, committed, wrong);

                int version = committed + 1;
                EncodeVersion(record, inserter, row, version);
                using var transaction = storage.BeginTransaction();
                storage.Update(transaction, location.PageId, location.SlotIndex, record);
                transaction.Commit();
                versions[(inserter, row)] = version;
            }
        })).ToArray();

        var pageWriter = Task.Run(() =>
        {
            while (Volatile.Read(ref inserting) > 0)
            {
                storage.WriteBackDirtyPages(64);
            }
        });

        await Task.WhenAll(inserters.Concat(updaters).Append(pageWriter));

        // Assert: every committed row reads back at its last committed version. Almost
        // every page has left the eight-page pool by now, so this reads the stream.
        deadline.IsCancellationRequested.ShouldBeFalse("the inserters did not reach the target size in time");
        storage.Data.Length.ShouldBeGreaterThanOrEqualTo(targetPages * (long)Page.Size);
        foreach (var (key, location) in locations)
        {
            Check(storage, key, location, versions[key], wrong);
        }

        wrong.Take(10).ShouldBeEmpty($"{wrong.Count} reads of {locations.Count} committed rows came back wrong");
        storage.BufferPool.CheckInvariants();
    }

    private static void Check(
        ConcurrentStorage storage,
        (int Inserter, int Row) key,
        (PageId PageId, int SlotIndex) location,
        int committed,
        ConcurrentQueue<string> wrong)
    {
        try
        {
            byte[] stored = storage.Read(location.PageId, location.SlotIndex);
            if (!HasVersion(stored, key.Inserter, key.Row, committed))
            {
                wrong.Enqueue($"row {key} reads version {BitConverter.ToInt32(stored, 8)}, last committed {committed}");
            }
        }
        catch (StorageException exception)
        {
            wrong.Enqueue($"row {key}: {exception.GetType().Name}: {exception.Message}");
        }
    }

    private static void EncodeVersion(byte[] record, int inserter, int row, int version)
    {
        BitConverter.TryWriteBytes(record.AsSpan(0), inserter);
        BitConverter.TryWriteBytes(record.AsSpan(4), row);
        BitConverter.TryWriteBytes(record.AsSpan(8), version);
        record.AsSpan(12).Fill(VersionFill(inserter, row, version));
    }

    private static bool HasVersion(byte[] record, int inserter, int row, int version)
        => record.Length >= 12
            && BitConverter.ToInt32(record, 0) == inserter
            && BitConverter.ToInt32(record, 4) == row
            && BitConverter.ToInt32(record, 8) == version
            && record.AsSpan(12).IndexOfAnyExcept(VersionFill(inserter, row, version)) < 0;

    private static byte VersionFill(int inserter, int row, int version) => (byte)((row * 31) + (inserter * 7) + (version * 13) + 1);

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

        storage.BufferPool.CheckInvariants();

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
            : base(StorageModel.Custom, data, journal, new StorageStream(new MemoryStream()), capacity)
        {
        }

        public static ConcurrentStorage Create(MemoryStream data, MemoryStream journal, int capacity)
        {
            var storage = new ConcurrentStorage(
                new StorageStream(new SimulatedDurableFileHandle(data)),
                new StorageStream(new SimulatedDurableFileHandle(journal)),
                capacity);
            storage.InitializeNew((Name)"concurrency");
            return storage;
        }

        /// <summary>
        /// Creates a storage over the production in-memory streams, which carry no
        /// durable-flush contract, so commits run with <see cref="StorageCommitDurability.None"/>.
        /// </summary>
        public static ConcurrentStorage CreateInMemory(int capacity)
        {
            var storage = new ConcurrentStorage(StorageStream.FromInMemory(), StorageStream.FromInMemory(), capacity);
            storage.ConfigureCommitDurability(null, "concurrency");
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

        public (PageId PageId, int SlotIndex) Insert(StorageTransaction transaction, ulong ownerId, byte[] data)
            => InsertRecord(transaction, ownerId, data);

        public void Update(StorageTransaction transaction, PageId pageId, int slotIndex, byte[] data)
            => UpdateRecord(transaction, pageId, slotIndex, data);

        public void Delete(StorageTransaction transaction, PageId pageId, int slotIndex)
            => DeleteRecord(transaction, pageId, slotIndex);

        public byte[] Read(PageId pageId, int slotIndex) => ReadRecord(pageId, slotIndex).ToArray();

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
