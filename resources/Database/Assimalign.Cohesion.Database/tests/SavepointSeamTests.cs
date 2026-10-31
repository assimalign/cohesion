using System;
using System.Runtime.CompilerServices;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Internal;
using Assimalign.Cohesion.Database.Sql;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Tests;

[Collection(nameof(DatabaseEventSourceCollection))]
public sealed class SavepointSeamTests
{
    [Theory(DisplayName = "Cohesion Test [Database] - Savepoint context replacement requires configuration and the same transaction (#1726)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UpdateSavepointContext_InvalidStateOrOwner_ShouldRefuse(bool foreignOwner)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("savepoint-seam", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        var coordinator = Coordinator(database);
        var context = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        var foreign = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        await using var transaction = new SeamTransaction(coordinator, context);

        // Act / Assert: distinguish configuration admission from context ownership.
        TransactionContext current = context;
        try
        {
            if (foreignOwner)
            {
                int callbacks = 0;
                var refusal = Should.Throw<ArgumentException>(() => transaction.Configure(() =>
                { callbacks++; transaction.Update(foreign); }));
                callbacks.ShouldBe(1);
                refusal.ParamName.ShouldBe("context");
            }
            else
            {
                Should.Throw<InvalidOperationException>(() => transaction.Update(context)).Message
                    .ShouldBe(ErrorMessages.SavepointContextConfigurationRequired);
            }
            current = coordinator.ReplaceIsolationLevel(context, IsolationLevel.ReadCommitted);
            transaction.Configure(() => transaction.Update(current)).ShouldBeTrue();
            transaction.Context.ShouldBeSameAs(current);
            await transaction.ExecuteSavepointAsync("scope", DatabaseSavepointOperation.Create, CancellationToken.None);
            int reached = 0;
            transaction.Configure(() => { reached++; transaction.Update(context); }).ShouldBeFalse();
            reached.ShouldBe(0, "live scopes refuse configuration before the identity callback");
            await transaction.RollbackAsync();
            Should.Throw<InvalidOperationException>(() => transaction.Update(context));
        }
        finally
        {
            if (current.State == TransactionState.Active) { await coordinator.RollbackAsync(current); }
            await coordinator.RollbackAsync(foreign);
        }
    }

    [Theory(DisplayName = "Cohesion Test [Database] - Each same-transaction invalid handle reaches exact replacement validation (#1726)")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task UpdateSavepointContext_StaleOrView_ShouldReachIdentityCheck(int kind)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("replacement-identity", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        var coordinator = Coordinator(database);
        var original = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        var older = coordinator.ReplaceIsolationLevel(original, IsolationLevel.Snapshot);
        var current = coordinator.ReplaceIsolationLevel(older, IsolationLevel.Snapshot);
        await using var transaction = new SeamTransaction(coordinator, original);
        var invalid = kind == 0 ? original : kind == 1 ? older : current.PinStatementSnapshot();
        int callbacks = 0;

        try
        {
            // Act
            var refusal = Should.Throw<ArgumentException>(() => transaction.Configure(() =>
            { callbacks++; transaction.Update(invalid); }));

            // Assert
            callbacks.ShouldBe(1, "configuration admission must reach the context identity branch");
            refusal.ParamName.ShouldBe("context");
            transaction.Context.ShouldBeSameAs(original);
            transaction.Configure(() => transaction.Update(current)).ShouldBeTrue();
            transaction.Context.ShouldBeSameAs(current);
            await transaction.RollbackAsync();
        }
        finally
        {
            // End through the known current handle even when a broken implementation installed
            // the view: disposal must not replace the identity assertion's failure.
            if (current.State == TransactionState.Active) { await coordinator.RollbackAsync(current); }
        }
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Running operations counts work, never control or configuration sentinels (#1726)")]
    public async Task RunningOperations_DuringControlAndFailedConfiguration_ShouldRemainNonnegative()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("savepoint-count", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        var coordinator = Coordinator(database);
        var context = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        await using var transaction = new SeamTransaction(coordinator, context);
        int observations = 0;
        Observe(coordinator, () => { observations++; transaction.Operations.ShouldBe(0); });

        // Act / Assert
        try { await transaction.ExecuteSavepointAsync("scope", DatabaseSavepointOperation.Create, CancellationToken.None); }
        finally { Observe(coordinator, null); }
        observations.ShouldBe(1);
        await transaction.ExecuteSavepointAsync("scope", DatabaseSavepointOperation.Release, CancellationToken.None);
        Should.Throw<InvalidOperationException>(() => transaction.Configure(() =>
        { transaction.Operations.ShouldBe(0); throw new InvalidOperationException("Injected configuration failure"); }));
        transaction.Operations.ShouldBe(0);
        transaction.Configure(() => { }).ShouldBeTrue();
        await transaction.RollbackAsync();
    }

    [Fact(DisplayName = "Cohesion Test [Database] - A blocked offline probe leaves root State and IsUsable readable (#1726)")]
    public async Task CreateRefusal_BlockedOfflineProbe_ShouldReleaseRootMonitor()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = SqlDatabaseEngine.Create("probe-monitor", new());
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        var coordinator = Coordinator(database);
        var context = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        await using var transaction = new SeamTransaction(coordinator, context);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        transaction.Probe = () => { entered.TrySetResult(); release.Wait(timeout.Token); return null; };
        var refusing = Task.Factory.StartNew(transaction.Refusal, timeout.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        // Act / Assert
        try
        {
            await entered.Task.WaitAsync(timeout.Token);
            bool readable = Monitor.TryEnter(Sync(transaction));
            try
            {
                readable.ShouldBeTrue("a blocked probe must not own root state");
                transaction.State.ShouldBe(TransactionState.Active);
                transaction.Usable.ShouldBeTrue();
            }
            finally { if (readable) { Monitor.Exit(Sync(transaction)); } }
        }
        finally { release.Set(); await refusing; transaction.Probe = null; }
        await transaction.RollbackAsync(timeout.Token);
    }

    [Theory(DisplayName = "Cohesion Test [Database] - Every model's direct typed controls and commit put disposal ahead of a live fence (#1726)")]
    [InlineData("Sql", "commit")]
    [InlineData("Sql", "refusal")]
    [InlineData("Sql", "create")]
    [InlineData("Sql", "release")]
    [InlineData("Sql", "undo")]
    [InlineData("Graph", "commit")]
    [InlineData("Graph", "refusal")]
    [InlineData("Graph", "create")]
    [InlineData("Graph", "release")]
    [InlineData("Graph", "undo")]
    [InlineData("Documents", "commit")]
    [InlineData("Documents", "refusal")]
    [InlineData("Documents", "create")]
    [InlineData("Documents", "release")]
    [InlineData("Documents", "undo")]
    [InlineData("KeyValuePair", "commit")]
    [InlineData("KeyValuePair", "refusal")]
    [InlineData("KeyValuePair", "create")]
    [InlineData("KeyValuePair", "release")]
    [InlineData("KeyValuePair", "undo")]
    [InlineData("Blob", "commit")]
    [InlineData("Blob", "refusal")]
    [InlineData("Blob", "create")]
    [InlineData("Blob", "release")]
    [InlineData("Blob", "undo")]
    public async Task ExecuteSavepointAsync_DisposedDatabase_ShouldWinOnDirectApi(string model, string operation)
    {
        // Arrange: a recorded index undo supplies a real admitted rollback failure for every model.
        await using DatabaseEngine engine = model switch
        {
            "Graph" => Graph.GraphDatabaseEngine.Create("direct-disposed", new()),
            "Documents" => Documents.DocumentDatabaseEngine.Create("direct-disposed", new()),
            "KeyValuePair" => KeyValuePair.KeyValueDatabaseEngine.Create("direct-disposed", new()),
            "Blob" => Blob.BlobDatabaseEngine.Create("direct-disposed", new()),
            _ => SqlDatabaseEngine.Create("direct-disposed", new()),
        };
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        var coordinator = database switch
        {
            Graph.GraphDatabase value => GraphCoordinator(value),
            Documents.DocumentDatabase value => DocumentCoordinator(value),
            KeyValuePair.KeyValueDatabase value => KeyValueCoordinator(value),
            Blob.BlobDatabase value => BlobCoordinator(value),
            _ => Coordinator((SqlDatabase)database),
        };
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var transaction = await session.BeginTransactionAsync(CancellationToken.None);
        var context = coordinator.GetOpenContexts().Single(value => value.Id == transaction.Id);
        await transaction.ExecuteSavepointAsync("scope", DatabaseSavepointOperation.Create, CancellationToken.None);
        coordinator.VersionStore.RecordIndexEntryCreated(context.Sequence, new NoopUndo(), ReadOnlyMemory<byte>.Empty, 0);
        Batch(coordinator.VersionStore, () => throw new System.IO.IOException("Injected undo failure"));
        try { await Record.ExceptionAsync(() => transaction.ExecuteSavepointAsync("scope", DatabaseSavepointOperation.Rollback, CancellationToken.None).AsTask()); }
        finally { Batch(coordinator.VersionStore, null); }
        TransactionCoordinator.IsPartialUndoPending(context).ShouldBeTrue();
        Interlocked.Exchange(ref Disposed(database), 1).ShouldBe(0);

        // Act / Assert
        try
        {
            await Should.ThrowAsync<ObjectDisposedException>(async () =>
            {
                if (operation == "commit") { await transaction.CommitAsync(); }
                else if (operation == "refusal") { throw transaction.CreateRefusal(); }
                else { await transaction.ExecuteSavepointAsync("scope", operation == "create" ? DatabaseSavepointOperation.Create
                    : operation == "release" ? DatabaseSavepointOperation.Release : DatabaseSavepointOperation.Rollback, CancellationToken.None); }
            });
        }
        finally { Volatile.Write(ref Disposed(database), 0); }
        await transaction.RollbackAsync();
    }

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Coordinator")]
    private static extern TransactionCoordinator GraphCoordinator(Graph.GraphDatabase database);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Coordinator")]
    private static extern TransactionCoordinator DocumentCoordinator(Documents.DocumentDatabase database);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Coordinator")]
    private static extern TransactionCoordinator KeyValueCoordinator(KeyValuePair.KeyValueDatabase database);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Coordinator")]
    private static extern TransactionCoordinator BlobCoordinator(Blob.BlobDatabase database);

    private sealed class NoopUndo : RecordVersionIndex
    {
        protected override ValueTask EraseCoreAsync(Storage.StorageTransaction bracket, ReadOnlyMemory<byte> key, ulong reference, TransactionSequence writer, CancellationToken token) => default;
        protected override ValueTask ClearDeleterCoreAsync(Storage.StorageTransaction bracket, ReadOnlyMemory<byte> key, ulong reference, TransactionSequence writer, CancellationToken token) => default;
    }

    [Theory(DisplayName = "Cohesion Test [Database] - Read-only exception Data preserves OOM identity, one-attempt admission and descendant trim (#1726)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteSavepointAsync_OomFailure_ShouldKeepIdentityAndAttemptLifetime(bool readOnlyData)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("admission-evidence", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE items (id INT)");
        await session.ExecuteAsync("BEGIN");
        await session.ExecuteAsync("SAVEPOINT outer");
        await session.ExecuteAsync("INSERT INTO items VALUES (1)");
        await session.ExecuteAsync("SAVEPOINT child");
        var coordinator = Coordinator(database);
        var injected = readOnlyData ? new ReadOnlyDataOom() : new OutOfMemoryException("Injected OOM");
        Batch(coordinator.VersionStore, () => throw injected);
        var transaction = session.CurrentTransaction!;

        // Act
        Exception? failure;
        try { failure = await Record.ExceptionAsync(() => transaction.ExecuteSavepointAsync("outer", DatabaseSavepointOperation.Rollback, CancellationToken.None).AsTask()); }
        finally { Batch(coordinator.VersionStore, null); }

        // Assert: no exception metadata is touched; a second attempt cannot consume old evidence.
        failure.ShouldBeSameAs(injected);
        injected.Data.Count.ShouldBe(0);
        await transaction.ExecuteSavepointAsync("outer", DatabaseSavepointOperation.Rollback, CancellationToken.None);
        var discarded = await Should.ThrowAsync<DatabaseException>(async () => await transaction.ExecuteSavepointAsync("child", DatabaseSavepointOperation.Release, CancellationToken.None));
        discarded.Diagnostics[0].Code.ShouldBe("CK04013");
        await session.ExecuteAsync("SAVEPOINT child");
        Validated(coordinator, () => throw injected);
        try { (await Record.ExceptionAsync(() => transaction.ExecuteSavepointAsync("outer", DatabaseSavepointOperation.Rollback, CancellationToken.None).AsTask())).ShouldBeSameAs(injected); }
        finally { Validated(coordinator, null); }
        await transaction.ExecuteSavepointAsync("child", DatabaseSavepointOperation.Release, CancellationToken.None);
        injected.Data.Count.ShouldBe(0);
        await session.ExecuteAsync("ROLLBACK");
    }

    [Fact(DisplayName = "Cohesion Test [Database] - A public coordinator caller leaves no token or coordinator on its exception (#1726)")]
    public async Task RollbackToSavepointAsync_PublicCaller_ShouldRetainNoExceptionEvidence()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("public-evidence", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        var coordinator = Coordinator(database);
        var context = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        var outer = await coordinator.CreateSavepointAsync(context);
        await coordinator.ReleaseSavepointAsync(context, outer);

        // Act: a real public-API refusal, without a fault-injection hook or root consumer.
        var failure = await Record.ExceptionAsync(() => coordinator.RollbackToSavepointAsync(context, outer).AsTask());

        // Assert
        TransactionCoordinator.GetUndoFailureCode(failure!).ShouldBe("CK04008");
        failure!.Data.Count.ShouldBe(0);
        context.State.ShouldBe(TransactionState.Active);
        await coordinator.RollbackAsync(context);
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_disposed")]
    private static extern ref int Disposed(DatabaseInstance database);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_UndoBatchApplied")]
    private static extern void Batch(RecordSpaceVersionStore store, Action? observer);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_UndoMarkerValidated")]
    private static extern void Validated(TransactionCoordinator coordinator, Action? observer);

    private sealed class ReadOnlyDataOom : OutOfMemoryException
    {
        private readonly System.Collections.IDictionary _data = new System.Collections.ObjectModel.ReadOnlyDictionary<object, object>(new System.Collections.Generic.Dictionary<object, object>());
        public override System.Collections.IDictionary Data => _data;
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_sync")]
    private static extern ref object Sync(DatabaseTransaction transaction);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Coordinator")]
    private static extern TransactionCoordinator Coordinator(SqlDatabase database);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_UndoControlAdmitted")]
    private static extern void Observe(TransactionCoordinator coordinator, Action? observer);

    private sealed class SeamTransaction : DatabaseTransaction
    {
        internal SeamTransaction(TransactionCoordinator coordinator, TransactionContext context) : base(coordinator, context) { }
        internal TransactionContext Context => KernelContext;
        internal Func<DatabaseException?>? Probe { get; set; }
        internal DatabaseException Refusal() => CreateRefusal();
        internal bool Usable => IsUsable;
        internal int Operations => RunningOperations;
        internal void Update(TransactionContext context) => UpdateSavepointContext(context);
        internal bool Configure(Action action) => TryConfigureIsolationLevel(IsolationLevel.ReadCommitted, () => { action(); return true; });
        protected override TransactionState GetKernelState() => KernelContext.State;
        protected override ValueTask CommitCoreAsync() => KernelCoordinator.CommitAsync(KernelContext);
        protected override ValueTask RollbackCoreAsync() => KernelCoordinator.RollbackAsync(KernelContext);
        protected override DatabaseException? GetOfflineRefusal() => Probe?.Invoke();
        protected override DatabaseException CreateAbortedException(Exception? cause, bool commit) => new("Aborted", cause);
    }
}
