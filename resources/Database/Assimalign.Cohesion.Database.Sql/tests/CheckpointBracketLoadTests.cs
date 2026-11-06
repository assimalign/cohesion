using System;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Tests;

namespace Assimalign.Cohesion.Database.Sql.Tests;

[Collection(nameof(SqlWorkerResilienceTests))]
public sealed class CheckpointBracketLoadTests
{
    private readonly ITestOutputHelper _output;
    public CheckpointBracketLoadTests(ITestOutputHelper output) { _output = output; }

    [Theory(DisplayName = "Cohesion Test [Database.Sql] - Checkpoint: four continuous writers on three Windows cores (#1283)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Checkpoint_FourContinuousWriters_ShouldRecordBoundedProgress(bool admissionPause)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("bracket-load", new()
        {
            CheckpointJournalSize = CheckpointLoad.JournalSize,
            CheckpointInterval = TimeSpan.FromHours(1),
            PageWriteBackInterval = TimeSpan.FromHours(1),
            MaintenanceInterval = TimeSpan.FromHours(1),
        });
        var database = await engine.CreateDatabaseAsync("load-" + Guid.NewGuid().ToString("N"), CancellationToken.None);
        await using var setup = await database.CreateSessionAsync();
        await setup.ExecuteAsync("CREATE TABLE items (id BIGINT PRIMARY KEY, value TEXT)");
        var sessions = new SqlDatabaseSession[4];
        try
        {
            for (int i = 0; i < sessions.Length; i++) { sessions[i] = await database.CreateSessionAsync(); }
            // Act / Assert: the shared harness records progress, bytes and checkpoint outcomes.
            await CheckpointLoad.RunAsync(database.DataStorage, database.Coordinator.Checkpoint, async (writer, next, token) =>
            {
                var session = sessions[writer];
                var result = await session.ExecuteAsync($"INSERT INTO items VALUES ({next * 4 + writer}, 'payload')", cancellationToken: token);
                result.Diagnostics.ShouldBeEmpty();
            }, _output.WriteLine, admissionPause);
        }
        finally
        {
            foreach (var session in sessions) { if (session is not null) { await session.DisposeAsync(); } }
        }
    }
}
