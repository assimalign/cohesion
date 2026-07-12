using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Indexing.Tests.TestObjects;

/// <summary>
/// A complete kernel harness for index tests: a storage instance, a transaction
/// manager, and the pairing between logical transaction contexts and their storage
/// transactions (the engine's job in production).
/// </summary>
public sealed class IndexTestHarness : IStorageTransactionSource, IAsyncDisposable
{
    private readonly Dictionary<ITransactionContext, IStorageTransaction> _pairs = new();
    private readonly object _sync = new();

    public IndexTestHarness(Stream? data = null, Stream? journal = null)
    {
        Storage = HarnessStorage.Create(data ?? new MemoryStream(), journal ?? new MemoryStream());
        LockManager = Transactions.LockManager.Create();
        Manager = TransactionManager.Create(TransactionLog.CreateInMemory(), LockManager, VersionStore.CreateInMemory());
        IndexManager = BTreeIndexManager.Create(new BTreeIndexManagerOptions
        {
            Storage = Storage,
            TransactionSource = this,
            LockManager = LockManager,
        });
    }

    private IndexTestHarness(HarnessStorage storage, IReadOnlyList<BTreeIndexRegistration> registrations)
    {
        Storage = storage;
        LockManager = Transactions.LockManager.Create();
        Manager = TransactionManager.Create(TransactionLog.CreateInMemory(), LockManager, VersionStore.CreateInMemory());
        IndexManager = BTreeIndexManager.Create(new BTreeIndexManagerOptions
        {
            Storage = Storage,
            TransactionSource = this,
            LockManager = LockManager,
            ExistingIndexes = registrations,
        });
    }

    public HarnessStorage Storage { get; }

    public ITransactionManager Manager { get; }

    public ILockManager LockManager { get; }

    public IIndexManager IndexManager { get; }

    /// <summary>
    /// Reopens crashed (or cleanly closed) storage bytes and re-attaches indexes
    /// from previously exported registrations — the catalog's job in production.
    /// </summary>
    public static IndexTestHarness Reopen(byte[] data, byte[] journal, IReadOnlyList<BTreeIndexRegistration> registrations)
    {
        // Expandable copies: recovery redo may extend the data file beyond the
        // bytes that were durable at the crash.
        var dataStream = new MemoryStream();
        dataStream.Write(data);
        var journalStream = new MemoryStream();
        journalStream.Write(journal);

        return new IndexTestHarness(HarnessStorage.Open(dataStream, journalStream), registrations);
    }

    public async Task<ITransactionContext> BeginAsync()
    {
        var context = await Manager.BeginAsync();
        var storageTransaction = Storage.BeginTransaction();

        lock (_sync)
        {
            _pairs[context] = storageTransaction;
        }

        return context;
    }

    public async Task CommitAsync(ITransactionContext context)
    {
        IStorageTransaction storageTransaction;
        lock (_sync)
        {
            storageTransaction = _pairs[context];
            _pairs.Remove(context);
        }

        storageTransaction.Commit();              // durability (pages + WAL)
        await Manager.CommitAsync(context);       // visibility (leaves the active table)
    }

    public async Task RollbackAsync(ITransactionContext context)
    {
        IStorageTransaction storageTransaction;
        lock (_sync)
        {
            storageTransaction = _pairs[context];
            _pairs.Remove(context);
        }

        storageTransaction.Rollback();             // restores pages (stamps revert)
        await Manager.RollbackAsync(context);      // releases locks, purges versions
    }

    /// <inheritdoc />
    public IStorageTransaction GetStorageTransaction(ITransactionContext context)
    {
        lock (_sync)
        {
            return _pairs[context];
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await Manager.DisposeAsync();
        Storage.Dispose();
    }

    /// <summary>
    /// Concrete storage over caller-supplied streams.
    /// </summary>
    public sealed class HarnessStorage : Database.Storage.Storage
    {
        private HarnessStorage(StorageStream data, StorageStream journal)
            : base(data, journal, new StorageStream(new MemoryStream()), bufferPoolCapacity: 64)
        {
        }

        public override StorageModel Model => StorageModel.Custom;

        public static HarnessStorage Create(Stream data, Stream journal)
        {
            var storage = new HarnessStorage(new StorageStream(data), new StorageStream(journal));
            storage.InitializeNew((Name)"index-harness");
            return storage;
        }

        public static HarnessStorage Open(Stream data, Stream journal)
        {
            var storage = new HarnessStorage(new StorageStream(data), new StorageStream(journal));
            storage.OpenExisting();
            return storage;
        }
    }
}
