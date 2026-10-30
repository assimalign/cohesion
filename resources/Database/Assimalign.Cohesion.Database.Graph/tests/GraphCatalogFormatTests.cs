using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Graph.Storage;

namespace Assimalign.Cohesion.Database.Graph.Tests;

public sealed class GraphCatalogFormatTests
{
    [Theory(DisplayName = "Cohesion Test [Graph] - Catalog format: v1 and future versions refuse before scrub and preserve every file")]
    [InlineData(1)]
    [InlineData(255)]
    public async Task OpenDatabaseAsync_UnsupportedCatalogVersion_ShouldRefuseWithoutWritingAsync(byte version)
    {
        // Arrange: alter a cleanly closed catalog record through a real storage bracket.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        string root = Path.Combine(Path.GetTempPath(), "cohesion-catalog-format-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var engine = GraphDatabaseEngine.Create("format", new() { RootPath = root }))
            {
                var database = await engine.CreateDatabaseAsync("graph", timeout.Token);
                await using var session = await database.CreateSessionAsync(timeout.Token);
                await session.ExecuteAsync("INSERT (:N {p:1})", cancellationToken: timeout.Token);
            }
            string directory = Path.Combine(root, "graph");
            using (var storage = GraphStorage.Open(File.Open(Path.Combine(directory, "graph.dat"), FileMode.Open, FileAccess.ReadWrite),
                File.Open(Path.Combine(directory, "graph.log"), FileMode.Open, FileAccess.ReadWrite),
                File.Open(Path.Combine(directory, "graph.bak"), FileMode.Open, FileAccess.ReadWrite), checkpointOnOpen: false))
            {
                using var records = storage.GetUnitIterator(0);
                records.MoveNext().ShouldBeTrue();
                var record = records.Current;
                byte[] bytes = record.Data.ToArray();
                bytes[17].ShouldBe((byte)2);
                bytes[17] = version;
                using var bracket = storage.BeginTransaction();
                storage.UpdateEntry(bracket, record.PageId, record.SlotIndex, bytes);
                bracket.Commit();
            }
            var before = Directory.GetFiles(directory).ToDictionary(path => path, File.ReadAllBytes);

            // Act
            await using var reopened = GraphDatabaseEngine.Create("format", new() { RootPath = root });
            var failure = await Should.ThrowAsync<DatabaseException>(async () => await reopened.OpenDatabaseAsync("graph", timeout.Token));

            // Assert
            failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG06019");
            failure.Message.ShouldContain("supports only format 2", Case.Sensitive);
            reopened.TryGetDatabase("graph", out _).ShouldBeFalse();
            await reopened.DisposeAsync();
            Directory.GetFiles(directory).Order().ShouldBe(before.Keys.Order());
            foreach (var (file, bytes) in before) { File.ReadAllBytes(file).ShouldBe(bytes); }
        }
        finally { if (Directory.Exists(root)) { Directory.Delete(root, recursive: true); } }
    }
}
