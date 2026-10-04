using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Assimalign.Cohesion.Database.Storage.Tests.TestObjects;

/// <summary>
/// A storage over write-through crash-simulation streams that can share a
/// <see cref="CrashPoint"/>: every write reaches the media as soon as it is issued (the worst
/// case for both the steal path and a checkpoint), and a scheduled write can be torn. A
/// storage that lost power is abandoned, not disposed: its shutdown would only throw again.
/// </summary>
internal sealed class TornStorage : Storage
{
    private readonly CrashSimulationStream _data;
    private readonly CrashSimulationStream _journal;

    private TornStorage(CrashSimulationStream data, CrashSimulationStream journal, int poolCapacity)
        : base(new StorageStream(data), new StorageStream(journal), new StorageStream(new MemoryStream()), poolCapacity)
    {
        _data = data;
        _journal = journal;
    }

    public override StorageModel Model => StorageModel.Custom;

    /// <summary>Gets the journal, for appending lifecycle records and reading it back.</summary>
    public IStorageJournal Log => WriteAheadLog;

    /// <summary>Gets the anchor pages the newest header generation chains.</summary>
    public IReadOnlyList<long> AnchorChainPages => HeaderState.AnchorChain;

    public static TornStorage Create(CrashPoint? point = null, int poolCapacity = 8)
    {
        var storage = new TornStorage(
            new CrashSimulationStream(writeThrough: true, point, "data"),
            new CrashSimulationStream(writeThrough: true, point, "journal"),
            poolCapacity);
        storage.InitializeNew((Name)"torn-harness");
        return storage;
    }

    /// <summary>
    /// Opens the durable images a crash left. The open-time checkpoint is deferred by default,
    /// so the recovered journal and anchor can be inspected.
    /// </summary>
    public static TornStorage Open((byte[] Data, byte[] Journal) images, CrashPoint? point = null, bool checkpointOnOpen = false, int poolCapacity = 8)
    {
        var storage = new TornStorage(
            new CrashSimulationStream(images.Data, writeThrough: true, point, "data"),
            new CrashSimulationStream(images.Journal, writeThrough: true, point, "journal"),
            poolCapacity);
        storage.OpenExisting(checkpointOnOpen);
        return storage;
    }

    /// <summary>Gets what a power loss right now would leave on the media.</summary>
    public (byte[] Data, byte[] Journal) CaptureDurable() => (_data.CaptureDurable(), _journal.CaptureDurable());

    /// <summary>Inserts one record per new page (each record nearly fills a page) in one committed bracket.</summary>
    public long[] FillPages(int count, ulong owner = 7)
    {
        var pages = new long[count];
        using var transaction = BeginTransaction();
        for (int i = 0; i < count; i++)
        {
            var record = new byte[7000];
            record.AsSpan().Fill((byte)(i + 1));
            pages[i] = (long)InsertRecord(transaction, owner, record).PageId;
        }

        transaction.Commit();
        return pages;
    }

    /// <summary>Deletes the record of each page <see cref="FillPages"/> filled in one committed bracket, which frees the pages.</summary>
    public void FreeAll(IEnumerable<long> pages)
    {
        using var transaction = BeginTransaction();
        foreach (long page in pages)
        {
            DeleteRecord(transaction, (PageId)page, 0);
        }

        transaction.Commit();
    }

    public (PageId PageId, int SlotIndex) Insert(string text)
    {
        using var transaction = BeginTransaction();
        var location = InsertRecord(transaction, Encoding.UTF8.GetBytes(text));
        transaction.Commit();
        return location;
    }

    public (PageId PageId, int SlotIndex) Insert(IStorageTransaction transaction, string text)
        => InsertRecord(transaction, Encoding.UTF8.GetBytes(text));

    public void Update(IStorageTransaction transaction, PageId pageId, int slotIndex, string text)
        => UpdateRecord(transaction, pageId, slotIndex, Encoding.UTF8.GetBytes(text));

    public string Read(PageId pageId, int slotIndex) => Encoding.UTF8.GetString(ReadRecord(pageId, slotIndex).Span);

    /// <summary>Reads every record of the shared space, in page order.</summary>
    public List<string> ScanText()
    {
        var results = new List<string>();
        using var iterator = GetUnitIterator(0);
        while (iterator.MoveNext())
        {
            results.Add(Encoding.UTF8.GetString(iterator.Current.Data.Span));
        }

        return results;
    }

    /// <summary>Counts the records an owner's chain holds.</summary>
    public int CountRecords(ulong owner)
    {
        int count = 0;
        using var iterator = GetUnitIterator(owner);
        while (iterator.MoveNext())
        {
            count++;
        }

        return count;
    }
}
