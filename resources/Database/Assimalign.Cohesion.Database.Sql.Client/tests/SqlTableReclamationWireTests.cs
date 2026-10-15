using System;
using System.Diagnostics.Tracing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Storage;
using Assimalign.Cohesion.Testing;

namespace Assimalign.Cohesion.Database.Sql.Client.Tests;

[Collection(nameof(SqlClientEventSourceCollection))]
public sealed class SqlTableReclamationWireTests
{
    [Fact(DisplayName = "Cohesion Test [Database.Sql.Client] - DROP is immediately visible over the wire and reclaimed space is reused without file growth")]
    public async Task ExecuteAsync_DropAndRecreate_ShouldReuseSpaceAfterWorkerCompletion()
    {
        // Arrange: close the seeded file so its measured length includes every allocation.
        string path = Path.Combine(Environment.CurrentDirectory, "_out", "1443", Guid.NewGuid().ToString("N"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await using (var seed = await SqlClientTestHarness.StartAsync(configureEngine: options => options.RootPath = path))
            {
                await using var connection = await seed.Client.ConnectAsync(timeout.Token);
                await connection.ExecuteAsync("CREATE TABLE retired (id INT PRIMARY KEY, payload VARCHAR(2000))", cancellationToken: timeout.Token);
                await FillAsync(connection, timeout.Token);
            }
            string dataFile = Path.Combine(path, "app", "sql.dat");
            long initialLength = new FileInfo(dataFile).Length;
            long reclaimed;

            // Act: an older wire transaction delays cleanup, while DROP is already visible.
            await using (var harness = await SqlClientTestHarness.StartAsync(configureEngine: options => options.RootPath = path, reopen: true))
            {
                using var events = new EventSourceRecorder(EventSource.GetSources().Single(source => source.Name == "Assimalign.Cohesion.Database.Sql"), EventLevel.Informational);
                await using var old = await harness.Client.ConnectAsync(timeout.Token);
                await old.ExecuteAsync("BEGIN", cancellationToken: timeout.Token);
                await using var connection = await harness.Client.ConnectAsync(timeout.Token);
                await connection.ExecuteAsync("DROP TABLE retired", cancellationToken: timeout.Token);
                await Should.ThrowAsync<SqlClientException>(async () => await connection.QueryAsync("SELECT * FROM retired", cancellationToken: timeout.Token));
                await connection.ExecuteAsync("INSERT INTO users VALUES (3, 'after-drop', 300)", cancellationToken: timeout.Token);
                events.Events.ShouldNotContain(entry => entry.EventId == 24);
                await old.ExecuteAsync("COMMIT", cancellationToken: timeout.Token);
                while (!events.Events.Any(entry => entry.EventId == 24)) { await Task.Delay(5, timeout.Token); }
                reclaimed = (long)events.Events.Single(entry => entry.EventId == 24).Payload![3]!;
                reclaimed.ShouldBeGreaterThan(32);
                await connection.ExecuteAsync("CREATE TABLE retired (id INT PRIMARY KEY, payload VARCHAR(2000))", cancellationToken: timeout.Token);
                await FillAsync(connection, timeout.Token);
                (await connection.QueryAsync("SELECT COUNT(*) FROM retired", cancellationToken: timeout.Token)).Single()[0].ShouldBe(300L);
            }

            // Assert: no growth; a second wire drop returns exactly its data and tree pages to the FSM.
            new FileInfo(dataFile).Length.ShouldBe(initialLength);
            await using (var harness = await SqlClientTestHarness.StartAsync(configureEngine: options => options.RootPath = path, reopen: true))
            {
                using var events = new EventSourceRecorder(EventSource.GetSources().Single(source => source.Name == "Assimalign.Cohesion.Database.Sql"), EventLevel.Informational);
                await using var connection = await harness.Client.ConnectAsync(timeout.Token);
                await connection.ExecuteAsync("DROP TABLE retired", cancellationToken: timeout.Token);
                while (!events.Events.Any(entry => entry.EventId == 24)) { await Task.Delay(5, timeout.Token); }
            }
            using var storage = SqlStorage.Open(File.Open(dataFile, FileMode.Open, FileAccess.ReadWrite),
                File.Open(Path.Combine(path, "app", "sql.log"), FileMode.Open, FileAccess.ReadWrite),
                File.Open(Path.Combine(path, "app", "sql.bak"), FileMode.Open, FileAccess.ReadWrite));
            storage.FreeSpaceMap.FreePageCount.ShouldBe(reclaimed);
        }
        finally { if (Directory.Exists(path)) { Directory.Delete(path, recursive: true); } }
    }

    private static async Task FillAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        string payload = new('x', 1800);
        await connection.ExecuteAsync("BEGIN", cancellationToken: cancellationToken);
        for (int row = 0; row < 300; row++)
        {
            await connection.ExecuteAsync($"INSERT INTO retired VALUES ({row}, '{payload}')", cancellationToken: cancellationToken);
        }
        await connection.ExecuteAsync("COMMIT", cancellationToken: cancellationToken);
    }
}
