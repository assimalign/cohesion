using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Storage.Tests;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.FileSystem;

namespace Assimalign.Cohesion.Database.Indexing.Tests.TestObjects;

/// <summary>
/// A complete kernel harness for index tests: a storage instance, a transaction
/// manager, and the pairing between logical transaction contexts and their storage
/// transactions (the engine's job in production).
/// </summary>
public sealed class IndexTestHarness : IAsyncDisposable
{
    private readonly Dictionary<TransactionContext, StorageTransaction> _pairs = new();
    private readonly object _sync = new();

    public IndexTestHarness(IFileSystemFileHandle? data = null, IFileSystemFileHandle? journal = null)
    {
        Storage = HarnessStorage.Create(data ?? new SimulatedDurableFileHandle(), journal ?? new SimulatedDurableFileHandle());
        LockManager = Transactions.LockManager.Create();
        Manager = TransactionManager.Create(LockManager, VersionStore.CreateInMemory());
        IndexManager = BTreeIndexManager.Create(new BTreeIndexManagerOptions
        {
            Storage = Storage,
            TransactionSource = GetStorageTransaction,
            LockManager = LockManager,
        });
    }

    private IndexTestHarness(HarnessStorage storage, IReadOnlyList<BTreeIndexRegistration> registrations)
    {
        Storage = storage;
        LockManager = Transactions.LockManager.Create();
        Manager = TransactionManager.Create(LockManager, VersionStore.CreateInMemory());
        IndexManager = BTreeIndexManager.Create(new BTreeIndexManagerOptions
        {
            Storage = Storage,
            TransactionSource = GetStorageTransaction,
            LockManager = LockManager,
            ExistingIndexes = registrations,
        });
    }

    public HarnessStorage Storage { get; }

    public TransactionManager Manager { get; }

    public LockManager LockManager { get; }

    public BTreeIndexManager IndexManager { get; }

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

        return new IndexTestHarness(HarnessStorage.Open(new SimulatedDurableFileHandle(dataStream), new SimulatedDurableFileHandle(journalStream)), registrations);
    }

    public async Task<TransactionContext> BeginAsync()
    {
        var context = await Manager.BeginAsync();
        var storageTransaction = Storage.BeginTransaction();

        lock (_sync)
        {
            _pairs[context] = storageTransaction;
        }

        return context;
    }

    public async Task CommitAsync(TransactionContext context)
    {
        StorageTransaction storageTransaction;
        lock (_sync)
        {
            storageTransaction = _pairs[context];
            _pairs.Remove(context);
        }

        storageTransaction.Commit();              // durability (pages + WAL)
        await Manager.CommitAsync(context);       // visibility (leaves the active table)
    }

    public async Task RollbackAsync(TransactionContext context)
    {
        StorageTransaction storageTransaction;
        lock (_sync)
        {
            storageTransaction = _pairs[context];
            _pairs.Remove(context);
        }

        storageTransaction.Rollback();             // restores pages (stamps revert)
        await Manager.RollbackAsync(context);      // releases locks, purges versions
    }

    /// <summary>
    /// Rolls a transaction back the way a model engine does after its statement
    /// brackets already committed (a multi-statement ROLLBACK): the transaction's
    /// physical writes stay durable, the caller's logical undo runs in a fresh
    /// bracket while the transaction still holds its locks, and only then does the
    /// transaction leave the active table as aborted.
    /// </summary>
    public async Task LogicalRollbackAsync(TransactionContext context, Func<StorageTransaction, Task> undo)
    {
        StorageTransaction storageTransaction;
        lock (_sync)
        {
            storageTransaction = _pairs[context];
            _pairs.Remove(context);
        }

        storageTransaction.Commit();

        using (var bracket = Storage.BeginTransaction())
        {
            await undo(bracket);
            bracket.Commit();
        }

        await Manager.RollbackAsync(context);
    }

    /// <summary>
    /// Resolves the storage transaction paired with a context: the harness's
    /// <see cref="BTreeIndexManagerOptions.TransactionSource"/>, the engine's job in production.
    /// </summary>
    /// <param name="context">The logical transaction context.</param>
    /// <returns>The paired storage transaction.</returns>
    public StorageTransaction GetStorageTransaction(TransactionContext context)
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
            : base(StorageModel.Custom, data, journal, new StorageStream(new MemoryStream()), bufferPoolCapacity: 64)
        {
        }

        public static HarnessStorage Create(IFileSystemFileHandle data, IFileSystemFileHandle journal)
        {
            var storage = new HarnessStorage(new StorageStream(data), new StorageStream(journal));
            storage.InitializeNew((Name)"index-harness");
            return storage;
        }

        public static HarnessStorage Open(IFileSystemFileHandle data, IFileSystemFileHandle journal)
        {
            var storage = new HarnessStorage(new StorageStream(data), new StorageStream(journal));
            storage.OpenExisting();
            return storage;
        }
    }
}
