using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Internal;
using Assimalign.Cohesion.Database.Sql;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Tests;

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

        // Act / Assert
        if (foreignOwner) { Should.Throw<ArgumentException>(() => transaction.Configure(() => transaction.Update(foreign))); }
        else { Should.Throw<InvalidOperationException>(() => transaction.Update(context)); }
        Should.Throw<InvalidOperationException>(() => transaction.Update(context.PinStatementSnapshot()));
        transaction.Configure(() => transaction.Update(coordinator.ReplaceIsolationLevel(context, IsolationLevel.ReadCommitted))).ShouldBeTrue();
        transaction.Context.Id.ShouldBe(context.Id);
        await transaction.ExecuteSavepointAsync("scope", DatabaseSavepointOperation.Create, CancellationToken.None);
        transaction.Configure(() => transaction.Update(context)).ShouldBeFalse();
        await transaction.RollbackAsync();
        Should.Throw<InvalidOperationException>(() => transaction.Update(context));
        await coordinator.RollbackAsync(foreign);
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

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Coordinator")]
    private static extern TransactionCoordinator Coordinator(SqlDatabase database);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_UndoControlAdmitted")]
    private static extern void Observe(TransactionCoordinator coordinator, Action? observer);

    private sealed class SeamTransaction : DatabaseTransaction
    {
        internal SeamTransaction(TransactionCoordinator coordinator, TransactionContext context) : base(coordinator, context) { }
        internal TransactionContext Context => KernelContext;
        internal int Operations => RunningOperations;
        internal void Update(TransactionContext context) => UpdateSavepointContext(context);
        internal bool Configure(Action action) => TryConfigureIsolationLevel(IsolationLevel.ReadCommitted, () => { action(); return true; });
        protected override TransactionState GetKernelState() => KernelContext.State;
        protected override ValueTask CommitCoreAsync() => KernelCoordinator.CommitAsync(KernelContext);
        protected override ValueTask RollbackCoreAsync() => KernelCoordinator.RollbackAsync(KernelContext);
        protected override DatabaseException? GetOfflineRefusal() => null;
        protected override DatabaseException CreateAbortedException(Exception? cause, bool commit) => new("Aborted", cause);
    }
}
