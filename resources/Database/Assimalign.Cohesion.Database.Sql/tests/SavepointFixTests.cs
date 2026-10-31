using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Sql.Tests;

[Collection("Savepoint engine races")]
public sealed class SavepointFixTests
{
    [Fact(DisplayName = "Cohesion Test [Sql] - Cancellation thrown by admitted undo is a recoverable fault, even after caller cancellation (#1726)")]
    public async Task ExecuteAsync_AdmittedUndoThrowsCancellation_ShouldReportFenceAndRecover()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var late = new CancellationTokenSource();
        await using var engine = SqlDatabaseEngine.Create("admitted-cancellation", new());
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        await session.ExecuteAsync("CREATE TABLE items (id INT PRIMARY KEY, n INT)", cancellationToken: timeout.Token);
        await session.ExecuteAsync("BEGIN", cancellationToken: timeout.Token);
        await session.ExecuteAsync("SAVEPOINT scope", cancellationToken: timeout.Token);
        await session.ExecuteAsync("INSERT INTO items VALUES (1, 1)", cancellationToken: timeout.Token);
        var context = session.CurrentTransaction!.Context;
        var injected = new OperationCanceledException("Injected cancellation fault after admission", late.Token);
        int visits = 0;
        SetBatchObserver(database.Coordinator.VersionStore, () => { visits++; late.Cancel(); throw injected; });

        // Act
        DatabaseException failure;
        try { failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("ROLLBACK TO scope", cancellationToken: late.Token)); }
        finally { SetBatchObserver(database.Coordinator.VersionStore, null); }

        // Assert
        visits.ShouldBe(1);
        failure.Diagnostics[0].Code.ShouldBe("CK04009");
        var cause = failure.InnerException.ShouldBeOfType<OperationCanceledException>()!;
        cause.ShouldBeSameAs(injected);
        cause.Data.Contains(typeof(TransactionSavepoint)).ShouldBeFalse();
        TransactionCoordinator.IsPartialUndoPending(context).ShouldBeTrue();
        await session.ExecuteAsync("ROLLBACK TO scope", cancellationToken: timeout.Token);
        await session.ExecuteAsync("INSERT INTO items VALUES (2, 2)", cancellationToken: timeout.Token);
        await session.ExecuteAsync("COMMIT", cancellationToken: timeout.Token);
        await using var observer = await database.CreateSessionAsync(timeout.Token);
        await using var result = (await observer.ExecuteAsync("SELECT n FROM items ORDER BY id", cancellationToken: timeout.Token)).ShouldBeAssignableTo<DatabaseResultSet>();
        var values = new List<long>();
        await foreach (var row in result.GetRowsAsync(timeout.Token)) { values.Add(Convert.ToInt64(row.GetValue(0))); }
        values.ShouldBe(new long[] { 2 });
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Named control I/O failure retains a recoverable fence and exact outcome (#1726)")]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ExecuteAsync_UndoIoFailure_ShouldTranslateAndRecover(bool adapter, bool wholeRollback)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = SqlDatabaseEngine.Create("undo-failure", new());
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        await session.ExecuteAsync("CREATE TABLE items (id INT PRIMARY KEY, n INT)", cancellationToken: timeout.Token);
        await session.ExecuteAsync("INSERT INTO items VALUES (1, 1)", cancellationToken: timeout.Token);
        await session.ExecuteAsync("BEGIN", cancellationToken: timeout.Token);
        await session.ExecuteAsync("INSERT INTO items VALUES (2, 2)", cancellationToken: timeout.Token);
        await session.ExecuteAsync("SAVEPOINT scope", cancellationToken: timeout.Token);
        await session.ExecuteAsync("INSERT INTO items VALUES (3, 3)", cancellationToken: timeout.Token);
        for (int i = 0; i < 78; i++) { await session.ExecuteAsync("UPDATE items SET n = n + 1 WHERE id = 3", cancellationToken: timeout.Token); }
        var coordinator = database.Coordinator;
        var injected = new SavepointIoUndo();
        if (adapter) { coordinator.VersionStore.RecordIndexEntryCreated(session.CurrentTransaction!.Context.Sequence, injected, ReadOnlyMemory<byte>.Empty, 0); }
        int batches = 0;
        if (!adapter) { SetBatchObserver(coordinator.VersionStore, () => { if (++batches == 2) { throw injected.Failure; } }); }

        // Act
        try
        {
            var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("ROLLBACK TO scope", cancellationToken: timeout.Token));
            failure.Diagnostics[0].Code.ShouldBe("CK04009");
            failure.InnerException.ShouldBeAssignableTo<DatabaseException>()!.Diagnostics[0].Code.ShouldBe("CS06041");
        }
        finally { SetBatchObserver(coordinator.VersionStore, null); injected.Fail = false; }

        // Assert
        (adapter ? injected.Calls : batches).ShouldBe(adapter ? 1 : 2);
        session.CurrentTransaction.ShouldNotBeNull().State.ShouldBe(TransactionState.Faulted);
        await session.ExecuteAsync(wholeRollback ? "ROLLBACK" : "ROLLBACK TO scope", cancellationToken: timeout.Token);
        if (wholeRollback) { await session.ExecuteAsync("BEGIN", cancellationToken: timeout.Token); }
        await session.ExecuteAsync("INSERT INTO items VALUES (4, 4)", cancellationToken: timeout.Token);
        await session.ExecuteAsync("COMMIT", cancellationToken: timeout.Token);
        await using var observer = await database.CreateSessionAsync(timeout.Token);
        await using var result = (await observer.ExecuteAsync("SELECT n FROM items ORDER BY id", cancellationToken: timeout.Token)).ShouldBeAssignableTo<DatabaseResultSet>();
        var values = new List<long>();
        await foreach (var row in result.GetRowsAsync(timeout.Token)) { values.Add(Convert.ToInt64(row.GetValue(0))); }
        values.ShouldBe(wholeRollback ? new long[] { 1, 4 } : new long[] { 1, 2, 4 });
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Pre-admission outer recovery failure preserves the inner kernel scope (#1726)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_CanceledOuterRetry_ShouldRetainInnerName(bool cancel)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("cancel-retry", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE items (id INT PRIMARY KEY, n INT)");
        await session.ExecuteAsync("BEGIN");
        await session.ExecuteAsync("SAVEPOINT outer");
        await session.ExecuteAsync("INSERT INTO items VALUES (1, 1)");
        await session.ExecuteAsync("SAVEPOINT inner");
        await session.ExecuteAsync("INSERT INTO items VALUES (3, 3)");
        var injected = new IOException("Injected undo failure");
        SetBatchObserver(database.Coordinator.VersionStore, () => throw injected);
        try { await Record.ExceptionAsync(() => session.ExecuteAsync("ROLLBACK TO inner").AsTask()); }
        finally { SetBatchObserver(database.Coordinator.VersionStore, null); }
        TransactionCoordinator.IsPartialUndoPending(session.CurrentTransaction!.Context).ShouldBeTrue();
        using var canceled = new CancellationTokenSource();
        SetValidatedObserver(database.Coordinator, () => { if (cancel) { canceled.Cancel(); } else { throw injected; } });

        // Act
        try
        {
            if (cancel) { await Should.ThrowAsync<OperationCanceledException>(async () => await session.ExecuteAsync("ROLLBACK TO outer", cancellationToken: canceled.Token)); }
            else { (await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("ROLLBACK TO outer"))).Diagnostics[0].Code.ShouldBe("CK04009"); }
        }
        finally { SetValidatedObserver(database.Coordinator, null); }

        // Assert
        await session.ExecuteAsync("ROLLBACK TO inner");
        await session.ExecuteAsync("COMMIT");
        await using var result = (await session.ExecuteAsync("SELECT n FROM items ORDER BY id")).ShouldBeAssignableTo<DatabaseResultSet>();
        var values = new List<long>();
        await foreach (var row in result.GetRowsAsync()) { values.Add(Convert.ToInt64(row.GetValue(0))); }
        values.ShouldBe(new long[] { 1 });
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Memory-only create and release allocation faults preserve identity and transaction outcome (#1726)")]
    [InlineData("SAVEPOINT other")]
    [InlineData("RELEASE scope")]
    public async Task ExecuteAsync_ControlAllocationFailure_ShouldKeepStatementPolicy(string control)
    {
        // Arrange: marker/lock/scope registration allocates; it performs no production I/O.
        await using var engine = SqlDatabaseEngine.Create("control-allocation", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("BEGIN");
        await session.ExecuteAsync("SAVEPOINT scope");
        var transaction = session.CurrentTransaction!;
        var injected = new OutOfMemoryException("Injected marker allocation failure");
        int admissions = 0;
        SetAdmissionObserver(database.Coordinator, () => { admissions++; throw injected; });

        // Act
        Exception? failure;
        try { failure = await Record.ExceptionAsync(() => session.ExecuteAsync(control).AsTask()); }
        finally { SetAdmissionObserver(database.Coordinator, null); }

        // Assert: SQL preserves an allocation-failed memory control; storage failures abort under #1364.
        failure.ShouldBeSameAs(injected);
        admissions.ShouldBe(1);
        transaction.State.ShouldBe(TransactionState.Active);
        transaction.Context.State.ShouldBe(TransactionState.Active);
        await session.ExecuteAsync("ROLLBACK");
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Disposed and offline databases outrank a partial undo fence on typed requests (#1726)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_FencedDatabaseUnavailable_ShouldKeepLifecyclePrecedence(bool offline)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("fence-precedence", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE items (id INT PRIMARY KEY, n INT)");
        await session.ExecuteAsync("BEGIN");
        await session.ExecuteAsync("SAVEPOINT scope");
        await session.ExecuteAsync("INSERT INTO items VALUES (1, 1)");
        SetBatchObserver(database.Coordinator.VersionStore, () => throw new IOException("Injected undo failure"));
        try { await Record.ExceptionAsync(() => session.ExecuteAsync("ROLLBACK TO scope").AsTask()); }
        finally { SetBatchObserver(database.Coordinator.VersionStore, null); }
        TransactionCoordinator.IsPartialUndoPending(session.CurrentTransaction!.Context).ShouldBeTrue();

        // Act / Assert
        if (offline)
        {
            database.DataStorage.TakeOffline(StorageOfflineCause.VersionPurgeFailures, "Injected offline database", new IOException("Offline"));
            var failure = await Should.ThrowAsync<DatabaseOfflineException>(async () => await session.ExecuteAsync(SqlQueryRequest.FromSql("SELECT 1")));
            failure.Diagnostics[0].Code.ShouldNotBe("CK04009");
            var refusal = session.CurrentTransaction!.CreateStatementRefusal();
            refusal.ShouldBeOfType<DatabaseOfflineException>();
        }
        else
        {
            // Observe the disposed admission boundary before teardown drains the fenced kernel.
            Interlocked.Exchange(ref Disposed(database), 1).ShouldBe(0);
            try
            {
                var closedDatabase = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(SqlQueryRequest.FromSql("SELECT 1")));
                closedDatabase.Diagnostics[0].Code.ShouldBe("CS04005");
            }
            finally { Volatile.Write(ref Disposed(database), 0); }
            // A close after the model precheck must retain the root session's own code.
            int closures = 0;
            SetValidatedObserver(database.Coordinator, () =>
            {
                closures++;
                Volatile.Write(ref Closed(session), true);
                CheckClosed(session);
            });
            try
            {
                var closing = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("ROLLBACK TO scope"));
                closing.Diagnostics[0].Code.ShouldBe("CK07004");
                closures.ShouldBe(1);
            }
            finally
            {
                SetValidatedObserver(database.Coordinator, null);
                Volatile.Write(ref Closed(session), false);
            }
            await session.DisposeAsync();
            var closed = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(SqlQueryRequest.FromSql("SELECT 1")));
            closed.Diagnostics[0].Code.ShouldNotBe("CK04009");
        }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Corruption during undo outranks CK04009 and aborts the writer (#1726)")]
    public async Task ExecuteAsync_CorruptUndo_ShouldAbortInsteadOfRecoverableFence()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("corrupt-undo", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE items (id INT PRIMARY KEY, n INT)");
        await session.ExecuteAsync("BEGIN");
        await session.ExecuteAsync("SAVEPOINT scope");
        await session.ExecuteAsync("INSERT INTO items VALUES (1, 1)");
        var transaction = session.CurrentTransaction!;
        int visits = 0;
        SetBatchObserver(database.Coordinator.VersionStore, () => { visits++; throw new StorageCorruptionException((PageId)42, "Injected damaged undo"); });

        // Act
        DatabaseCorruptionException failure;
        try { failure = await Should.ThrowAsync<DatabaseCorruptionException>(async () => await session.ExecuteAsync("ROLLBACK TO scope")); }
        finally { SetBatchObserver(database.Coordinator.VersionStore, null); }

        // Assert
        visits.ShouldBe(1);
        failure.Diagnostics[0].Code.ShouldBe("CK06003");
        transaction.Context.State.ShouldNotBe(TransactionState.Active);
        var wrapped = new DatabaseException("Pending undo", [new Types.Diagnostic
        { Code = "CK04009", Message = "Pending undo", Severity = Types.DiagnosticSeverity.Error }], failure);
        Assimalign.Cohesion.Database.Internal.DatabaseFailure.IsStorageFailure(wrapped).ShouldBeTrue("corruption aborts even below a CK04009 wrapper (#1364)");
        await session.ExecuteAsync("ROLLBACK");
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Named control parameter I/O follows #1364 and aborts the explicit transaction (#1726)")]
    [InlineData("SAVEPOINT other")]
    [InlineData("RELEASE scope")]
    public async Task ExecuteAsync_ControlParameterIoFailure_ShouldAbortWithStatementCode(string control)
    {
        // Arrange: an application-supplied parameter map can fail while it reads its values.
        await using var engine = SqlDatabaseEngine.Create("control-parameters", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("BEGIN");
        await session.ExecuteAsync("SAVEPOINT scope");
        var transaction = session.CurrentTransaction!;
        var injected = new IOException("Injected parameter-source I/O failure");
        var parameters = new FailingSavepointParameters(injected);

        // Act
        DatabaseException failure;
        failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(control, parameters));

        // Assert: #1364 applies to named controls, including parameter reads before admission.
        parameters.Reads.ShouldBe(1);
        failure.Diagnostics[0].Code.ShouldBe("CS06041");
        failure.InnerException.ShouldBeSameAs(injected);
        transaction.State.ShouldBe(TransactionState.Faulted);
        transaction.Context.State.ShouldBe(TransactionState.RolledBack);
        await session.ExecuteAsync("ROLLBACK");
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Every named CK04009 refusal keeps its complete statement location (#1726)")]
    [InlineData("SAVEPOINT next")]
    [InlineData("RELEASE scope")]
    [InlineData("ROLLBACK TO scope")]
    public async Task ExecuteAsync_NamedFence_ShouldKeepLocation(string control)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("control-location", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE items (id INT)");
        await session.ExecuteAsync("BEGIN");
        await session.ExecuteAsync("SAVEPOINT scope");
        await session.ExecuteAsync("INSERT INTO items VALUES (1)");
        SetBatchObserver(database.Coordinator.VersionStore, () => throw new IOException("Injected undo failure"));
        try
        {
            await Record.ExceptionAsync(() => session.ExecuteAsync("ROLLBACK TO scope").AsTask());
            // Act
            var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(control));
            // Assert
            failure.Diagnostics[0].Code.ShouldBe("CK04009");
            failure.Diagnostics[0].Start.ShouldBe(0);
            failure.Diagnostics[0].End.ShouldBe(control.Length);
        }
        finally { SetBatchObserver(database.Coordinator.VersionStore, null); }
        await session.ExecuteAsync("ROLLBACK");
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_disposed")]
    private static extern ref int Disposed(DatabaseInstance database);
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_closed")]
    private static extern ref bool Closed(DatabaseSession session);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "ThrowIfClosed")]
    private static extern void CheckClosed(DatabaseSession session);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_UndoBatchApplied")]
    private static extern void SetBatchObserver(RecordSpaceVersionStore store, Action? observer);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_UndoMarkerValidated")]
    private static extern void SetValidatedObserver(TransactionCoordinator coordinator, Action? observer);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_UndoControlAdmitted")]
    private static extern void SetAdmissionObserver(TransactionCoordinator coordinator, Action? observer);

    private sealed class SavepointIoUndo : RecordVersionIndex
    {
        internal readonly IOException Failure = new("Injected recorded undo failure");
        internal bool Fail = true;
        internal int Calls;
        protected override ValueTask EraseCoreAsync(StorageTransaction transaction, ReadOnlyMemory<byte> key, ulong reference, TransactionSequence writer, CancellationToken token)
        { Calls++; if (Fail) { throw Failure; } return default; }
        protected override ValueTask ClearDeleterCoreAsync(StorageTransaction transaction, ReadOnlyMemory<byte> key, ulong reference, TransactionSequence writer, CancellationToken token) => default;
    }
}
