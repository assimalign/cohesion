using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Client;
using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Client;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

[CollectionDefinition("SQL execution diagnostic interleavings", DisableParallelization = true)]
[Collection("SQL execution diagnostic interleavings")]
public sealed class SqlExecutionDiagnosticInterleavingTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - DDL: a replaced table is retryable locally and over the wire (#1653)")]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ExecuteAsync_TableReplacedDuringLockWait_ShouldCarrySerializationFailure(bool wire, bool rename)
    {
        // Arrange: the lock-wait event orders replacement after binding and before execution resumes.
        await using var harness = await ServerTestHarness.StartAsync();
        harness.Engine.TryGetDatabase(ServerTestHarness.DatabaseName, out var database).ShouldBeTrue();
        await using var session = await database!.CreateSessionAsync(TestTimeout.Token());
        await using var client = SqlClient.Create(new SqlClientOptions
        {
            Settings = new DatabaseConnectionSettings { Database = ServerTestHarness.DatabaseName, Principal = "diagnostics", EndPoint = harness.Listener.EndPoint },
            ConnectionFactory = harness.Listener.CreateFactory(),
        });
        await using var connection = await client.ConnectAsync(TestTimeout.Token());
        using var waits = new LockWaitListener(ServerTestHarness.DatabaseName);
        var elapsed = Stopwatch.StartNew();
        int replacements = 0;
        string sql = rename ? "ALTER TABLE replacement RENAME TO renamed" : "DROP TABLE replacement";
        do
        {
            var table = await database.Catalog.CreateTableAsync("dbo", "replacement",
                [new SqlCatalogColumn("id", new DatabaseTypeInfo(DatabaseType.Int32))], [], TestTimeout.Token());
            var holder = await database.Coordinator.BeginAsync(IsolationLevel.ReadCommitted, TestTimeout.Token());
            var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            waits.Observed = written => { if (written.EventName == "LockWaitStart") { waiting.TrySetResult(); } };
            await database.Coordinator.LockManager.AcquireAsync(holder.Sequence, LockResource.Object(table.ObjectId), LockMode.Exclusive, TestTimeout.Token());
            Task<IReadOnlyList<Diagnostic>> failed = CaptureAsync();
            try
            {
                // Act
                await waiting.Task.WaitAsync(TestTimeout.Token());
                await database.Catalog.DropTableAsync("dbo", "replacement", TestTimeout.Token());
                var replacement = await database.Catalog.CreateTableAsync("dbo", "replacement",
                    [new SqlCatalogColumn("id", new DatabaseTypeInfo(DatabaseType.Int32))], [], TestTimeout.Token());
                replacement.ObjectId.ShouldNotBe(table.ObjectId);
            }
            finally { await database.Coordinator.RollbackAsync(holder, CancellationToken.None); }

            // Assert
            var diagnostic = (await failed).ShouldHaveSingleItem();
            diagnostic.Code.ShouldBe("CS04064");
            diagnostic.Start.ShouldBe(0);
            diagnostic.End.ShouldBe(sql.Length);
            database.Catalog.TryGetTable("dbo", "replacement", out _).ShouldBeTrue();
            await database.Catalog.DropTableAsync("dbo", "replacement", TestTimeout.Token());
            replacements++;
            elapsed.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(30));
        } while (elapsed.Elapsed < TimeSpan.FromSeconds(1) || replacements < 2);
        replacements.ShouldBeGreaterThanOrEqualTo(2);

        async Task<IReadOnlyList<Diagnostic>> CaptureAsync()
        {
            if (wire)
            {
                var error = await Should.ThrowAsync<SqlClientException>(async () => await connection.ExecuteAsync(sql, cancellationToken: TestTimeout.Token()));
                error.ConnectionUsable.ShouldBeTrue();
                return error.Diagnostics;
            }
            return (await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(sql, cancellationToken: TestTimeout.Token()))).Diagnostics;
        }
    }
}
