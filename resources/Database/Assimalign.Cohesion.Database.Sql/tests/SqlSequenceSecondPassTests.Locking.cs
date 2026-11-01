using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Types;
using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlSequenceSecondPassTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - Sequence DDL: an unrelated definition lease is waited out")]
    [InlineData(false, false)][InlineData(false, true)][InlineData(true, false)][InlineData(true, true)]
    public async Task Execute_SequenceDdlDuringDefinition_ShouldWait(bool schemaDrop, bool dropSequence)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        await using var engine = SqlDatabaseEngine.Create("second-pass", new());
        var db = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var holder = await db.CreateSessionAsync(timeout.Token);
        await using var changer = await db.CreateSessionAsync(timeout.Token);
        await Exec(holder, "CREATE SEQUENCE s");
        await Exec(holder, "CREATE SCHEMA other");
        await Exec(holder, "CREATE SEQUENCE other.s");
        await Exec(holder, "CREATE TABLE other.t(k INT)");
        Action hold = () => { entered.Set(); release.Wait(timeout.Token); };
        if (schemaDrop) { SqlPlanExecutor.SchemaDefinitionLeaseEntered = hold; }
        else { SqlDatabaseSession.DefinitionLeaseEntered = hold; }
        var attempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        SqlPlanExecutor.BeforeSequenceMetadataLease = () => attempted.TrySetResult();
        Task definition = DedicatedTestTask.Run(() => Exec(holder, schemaDrop ? "DROP SCHEMA other CASCADE" : "CREATE VIEW v AS SELECT 1 AS k", timeout.Token), timeout.Token);
        Task? change = null;
        try
        {
            entered.Wait(timeout.Token);
            // Act
            change = Exec(changer, dropSequence ? "DROP SEQUENCE s" : "ALTER SEQUENCE s CACHE 2", timeout.Token);
            await attempted.Task.WaitAsync(timeout.Token);
            change.IsCompleted.ShouldBeFalse();
            release.Set();
            await Task.WhenAll(definition, change).WaitAsync(timeout.Token);

            // Assert
            db.Catalog.FindSequence("dbo", "s").ShouldSatisfyAllConditions(sequence =>
            {
                if (dropSequence) { sequence.ShouldBeNull(); }
                else { sequence.ShouldNotBeNull().Options.Cache.ShouldBe(2); }
            });
        }
        finally
        {
            release.Set();
            SqlDatabaseSession.DefinitionLeaseEntered = null;
            SqlPlanExecutor.SchemaDefinitionLeaseEntered = null;
            SqlPlanExecutor.BeforeSequenceMetadataLease = null;
            try { await definition; } catch { }
            if (change is not null) { try { await change; } catch { } }
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Definition lease wait: cancellation and lock timeout release sequence grants")]
    [InlineData(false)][InlineData(true)]
    public async Task Execute_SequenceMetadataWait_ShouldHonorCancellationAndLockTimeout(bool expireLockTimeout)
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(guard.Token);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        await using var engine = SqlDatabaseEngine.Create("second-pass", new());
        var db = await engine.CreateDatabaseAsync("db", guard.Token);
        await using var holder = await db.CreateSessionAsync(guard.Token);
        await using var changer = await db.CreateSessionAsync(guard.Token);
        await Exec(holder, "CREATE SEQUENCE s");
        changer.LockTimeout = expireLockTimeout ? TimeSpan.FromSeconds(35) : TimeSpan.Zero;
        SqlDatabaseSession.DefinitionLeaseEntered = () => { entered.Set(); release.Wait(guard.Token); };
        int waits = 0;
        SqlDdlInterlock.MetadataWaitStarting = timer =>
        {
            waits++;
            if (expireLockTimeout) { timer.ShouldNotBeNull().Cancel(); }
            else { caller.Cancel(); }
        };
        Task definition = DedicatedTestTask.Run(() => Exec(holder, "CREATE VIEW v AS SELECT 1 AS k", guard.Token), guard.Token);
        try
        {
            entered.Wait(guard.Token);
            // Act / Assert
            if (expireLockTimeout)
            {
                var error = await Should.ThrowAsync<SqlTimeoutException>(() => Exec(changer, "ALTER SEQUENCE s CACHE 2", caller.Token));
                error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS04044");
            }
            else { await Should.ThrowAsync<OperationCanceledException>(() => Exec(changer, "ALTER SEQUENCE s CACHE 2", caller.Token)); }
            waits.ShouldBe(1);
            release.Set();
            await definition.WaitAsync(guard.Token);
            SqlDdlInterlock.MetadataWaitStarting = null;
            await Exec(changer, "ALTER SEQUENCE s CACHE 3", guard.Token);
            db.Catalog.FindSequence("dbo", "s").ShouldNotBeNull().Options.Cache.ShouldBe(3);
        }
        finally
        {
            release.Set();
            SqlDatabaseSession.DefinitionLeaseEntered = null;
            SqlDdlInterlock.MetadataWaitStarting = null;
            try { await definition; } catch { }
        }
    }

}
