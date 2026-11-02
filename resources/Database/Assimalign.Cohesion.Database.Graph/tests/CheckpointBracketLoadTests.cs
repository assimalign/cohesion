using System;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Tests;

namespace Assimalign.Cohesion.Database.Graph.Tests;

[Collection(nameof(GraphWorkerResilienceCollection))]
public sealed class CheckpointBracketLoadTests
{
    private readonly ITestOutputHelper _output;
    public CheckpointBracketLoadTests(ITestOutputHelper output) { _output = output; }

    [Fact(DisplayName = "Cohesion Test [Database.Graph] - Checkpoint: four continuous writers on three Windows cores (#1283)")]
    public async Task Checkpoint_FourContinuousWriters_ShouldRecordBoundedProgress()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("bracket-load", new()
        {
            CheckpointJournalSize = CheckpointLoad.JournalSize,
            CheckpointInterval = TimeSpan.FromHours(1),
            PageWriteBackInterval = TimeSpan.FromHours(1),
            MaintenanceInterval = TimeSpan.FromHours(1),
        });
        var database = await engine.CreateDatabaseAsync("load", CancellationToken.None);
        
        var sessions = new GraphDatabaseSession[4];
        for (int i = 0; i < sessions.Length; i++) { sessions[i] = await database.CreateSessionAsync(); }
        try
        {
        // Act / Assert: the shared harness records progress, bytes and checkpoint outcomes.
        await CheckpointLoad.RunAsync(database.DataStorage, async (writer, next, token) =>
        {
            var session = sessions[writer];
            var result = await session.ExecuteAsync("CREATE (:Item {value: 'payload'})", cancellationToken: token);
            result.Diagnostics.ShouldBeEmpty();
        }, _output.WriteLine);
        }
        finally
        {
            foreach (var session in sessions) { await session.DisposeAsync(); }
        }
    }
}
