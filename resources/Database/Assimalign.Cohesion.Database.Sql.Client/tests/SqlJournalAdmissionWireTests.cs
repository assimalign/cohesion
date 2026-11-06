using System;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Testing;

using PhysicalStorage = Assimalign.Cohesion.Database.Storage.Storage;

namespace Assimalign.Cohesion.Database.Sql.Client.Tests;

[Collection(nameof(SqlClientEventSourceCollection))]
public sealed class SqlJournalAdmissionWireTests
{
    [Theory(DisplayName = "Cohesion Test [Sql.Client] - CK04014 preserves an explicit transaction and its TCP session (#1283)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_PausedAdmissionDeadline_ShouldKeepTransactionAndSessionUsable(bool explicitTransaction)
    {
        // Arrange
        using var ceiling = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await using var harness = await SqlClientTestHarness.StartAsync(tcp: true, configureEngine: options =>
        { options.CheckpointJournalSize = 0; options.CheckpointInterval = TimeSpan.FromHours(1); options.StatementTimeout = TimeSpan.FromHours(1); });
        await using var connection = await harness.Client.ConnectAsync(ceiling.Token);
        var database = await harness.Engine.OpenDatabaseAsync(SqlClientTestHarness.DatabaseName, ceiling.Token);
        if (explicitTransaction)
        {
            await connection.ExecuteAsync("BEGIN", cancellationToken: ceiling.Token);
            await connection.ExecuteAsync("INSERT INTO users VALUES (5, 'before', 500)", cancellationToken: ceiling.Token);
        }
        var coordinator = Coordinator(database);
        var storage = DataStorage(coordinator);
        var gate = TransactionCheckpointInterlock.ForStorage(storage);
        using var outside = storage.BeginTransaction("OutsideTest");
        gate.JournalSizeLimit = 2;
        gate.ObserveAdmissionPause().ShouldBeTrue();
        var source = EventSource.GetSources().Single(s => s.Name == "Assimalign.Cohesion.Database.Transactions");
        using var recorder = new EventSourceRecorder(source, EventLevel.Verbose);
        var writing = connection.ExecuteAsync("INSERT INTO users VALUES (3, 'paused', 300)", cancellationToken: ceiling.Token).AsTask();
        try
        {
            while (!recorder.Events.Any(e => e.EventId == 25)) { await Task.Delay(1, ceiling.Token); }
            // Act: expire the actual server statement timer after it records admission wait.
            var session = harness.Server.Sessions.Single().DatabaseSession.ShouldBeOfType<SqlDatabaseSession>();
            ExpireStatementTimer(session);
            var refusal = await Should.ThrowAsync<SqlClientException>(() => writing.WaitAsync(ceiling.Token));
            // Assert: the failed statement leaves the authenticated session and transaction ready.
            refusal.Diagnostics.ShouldContain(d => d.Code == "CK04014");
            refusal.ConnectionUsable.ShouldBeTrue();
            (await connection.QueryAsync("SELECT * FROM users", cancellationToken: ceiling.Token)).Count.ShouldBe(explicitTransaction ? 3 : 2);
            outside.Rollback();
            coordinator.Checkpoint(ceiling.Token);
            await connection.ExecuteAsync("INSERT INTO users VALUES (4, 'resumed', 400)", cancellationToken: ceiling.Token);
            if (explicitTransaction) { await connection.ExecuteAsync("COMMIT", cancellationToken: ceiling.Token); }
            (await connection.QueryAsync("SELECT * FROM users", cancellationToken: ceiling.Token)).Count.ShouldBe(explicitTransaction ? 4 : 3);
            harness.Server.Sessions.Count.ShouldBe(1);
        }
        finally
        {
            gate.JournalSizeLimit = 0;
            if (outside.IsActive) { outside.Rollback(); }
            try { await writing.WaitAsync(ceiling.Token); } catch (SqlClientException) { }
        }
    }

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "ExpireStatementTimerForTest")]
    private static extern void ExpireStatementTimer(SqlDatabaseSession session);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_storage")]
    private static extern ref PhysicalStorage DataStorage(TransactionCoordinator coordinator);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Coordinator")]
    private static extern TransactionCoordinator Coordinator(SqlDatabase database);
}
