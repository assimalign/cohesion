using System;
using System.IO;

using Assimalign.Cohesion.FileSystem;

namespace Assimalign.Cohesion.Database.Storage.Tests.TestObjects;

/// <summary>
/// Minimal concrete storage exposing the journal for durability assertions.
/// </summary>
internal sealed class WorkerStorage : Storage
{
    private WorkerStorage(StorageStream data, StorageStream journal)
        : base(StorageModel.Sql, data, journal, new StorageStream(new MemoryStream())) { }


    public StorageJournal Wal => WriteAheadLog;

    public static WorkerStorage Create(Stream data, Stream journal)
        => Create(data, new SimulatedDurableFileHandle(journal));

    public static WorkerStorage Create(Stream data, IFileSystemFileHandle journal)
    {
        var storage = new WorkerStorage(new StorageStream(new SimulatedDurableFileHandle(data)), new StorageStream(journal));
        storage.InitializeNew((Name)"worker-support");
        return storage;
    }

    public (PageId PageId, int SlotIndex) Insert(StorageTransaction transaction, ReadOnlySpan<byte> data)
        => InsertRecord(transaction, data);

    public (PageId PageId, int SlotIndex) Insert(ReadOnlySpan<byte> data)
        => InsertRecord(data);
}
