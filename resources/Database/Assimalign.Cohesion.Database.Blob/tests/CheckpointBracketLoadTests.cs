using System;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Tests;

namespace Assimalign.Cohesion.Database.Blob.Tests;

[Collection(nameof(BlobDatabaseEventSourceCollection))]
public sealed class CheckpointBracketLoadTests
{
    private readonly ITestOutputHelper _output;
    public CheckpointBracketLoadTests(ITestOutputHelper output) { _output = output; }

    [Fact(DisplayName = "Cohesion Test [Database.Blob] - Checkpoint: four continuous writers on three Windows cores (#1283)")]
    public async Task Checkpoint_FourContinuousWriters_ShouldRecordBoundedProgress()
    {
        // Arrange
        await using var engine = BlobDatabaseEngine.Create("bracket-load", new()
        {
            CheckpointJournalSize = CheckpointLoad.JournalSize,
            CheckpointInterval = TimeSpan.FromHours(1),
            PageWriteBackInterval = TimeSpan.FromHours(1),
            MaintenanceInterval = TimeSpan.FromHours(1),
        });
        var database = await engine.CreateDatabaseAsync("load", CancellationToken.None);
        await using var setup = await database.CreateSessionAsync();
        await setup.CreateContainerAsync("files");
        var sessions = new BlobDatabaseSession[4];
        for (int i = 0; i < sessions.Length; i++) { sessions[i] = await database.CreateSessionAsync(); }
        try
        {
        // Act / Assert: the shared harness records progress, bytes and checkpoint outcomes.
        await CheckpointLoad.RunAsync(database.DataStorage, async (writer, next, token) =>
        {
            var session = sessions[writer];
            var files = await session.GetContainerAsync("files", token);
            await using var upload = await files.OpenWriteAsync($"{writer}-{next}", cancellationToken: token);
            await upload.WriteAsync(new byte[1024], token);
        }, _output.WriteLine);
        }
        finally
        {
            foreach (var session in sessions) { await session.DisposeAsync(); }
        }
    }
}
