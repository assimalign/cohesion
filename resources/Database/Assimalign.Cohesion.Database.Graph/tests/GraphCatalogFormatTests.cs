using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Graph.Storage;
using Assimalign.Cohesion.Database.Graph.Internal;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Graph.Tests;

public sealed class GraphCatalogFormatTests
{
    [Theory(DisplayName = "Cohesion Test [Graph] - Catalog format: uncheckpointed v1 images refuse before logical scrub")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task OpenDatabaseAsync_UncheckpointedVersionOne_ShouldRefuseBeforeLogicalScrubAsync(int kind)
    {
        // Arrange / Act
        var failure = await RefuseUncheckpointedCatalogAsync(kind);

        // Assert: physical redo precedes this gate until Storage #1744; file equality is not asserted.
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG06019");
        failure.Message.ShouldContain("supports only format 2", Case.Sensitive);
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Catalog format: uncheckpointed v1 refusal names the database")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task OpenDatabaseAsync_UncheckpointedVersionOne_ShouldNameDatabaseAsync(int kind)
    {
        // Arrange / Act
        var failure = await RefuseUncheckpointedCatalogAsync(kind);

        // Assert
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG06019");
        failure.Message.ShouldContain("LegacyGraph", Case.Sensitive);
        failure.Diagnostics[0].Message.ShouldBe(failure.Message);
    }

    private static async Task<DatabaseException> RefuseUncheckpointedCatalogAsync(int kind)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        string root = Path.Combine(Path.GetTempPath(), "cohesion-catalog-wal-" + Guid.NewGuid().ToString("N"));
        try
        {
            string directory = Path.Combine(root, "legacygraph");
            Directory.CreateDirectory(directory);
            using (var data = new MemoryStream())
            using (var journal = new MemoryStream())
            using (var backup = new MemoryStream())
            using (var storage = GraphStorage.Create(data, journal, backup, "LegacyGraph"))
            {
                // Review probe kinds: 0 label, 1 relationship type, 3 index, 4 named type constraint.
                Guid id = Guid.NewGuid();
                CatalogRecord record = kind switch
                {
                    0 => new(label: new(id, "N")),
                    1 => new(relationshipType: new(id, "T")),
                    3 => new(index: new(id, "index", "p")),
                    _ => new(constraint: new(id, "typed", "p", DatabaseType.Int64)),
                };
                byte[] bytes = GraphCatalogCodec.Encode(record, new TransactionSequence(7));
                bytes[17] = 1;
                using (var bracket = storage.BeginTransaction())
                {
                    storage.InsertEntry(bracket, bytes);
                    bracket.Commit();
                }
                storage.FlushPendingCommits();
                data.ToArray().AsSpan().IndexOf(bytes).ShouldBe(-1);
                journal.ToArray().AsSpan().IndexOf(bytes).ShouldBeGreaterThanOrEqualTo(0);
                // Copy live streams before disposal/checkpoint: the only v1 record is in the WAL.
                File.WriteAllBytes(Path.Combine(directory, "graph.dat"), data.ToArray());
                File.WriteAllBytes(Path.Combine(directory, "graph.log"), journal.ToArray());
                File.WriteAllBytes(Path.Combine(directory, "graph.bak"), backup.ToArray());
                journal.Length.ShouldBeGreaterThan(0);
            }
            await using var reopened = GraphDatabaseEngine.Create("format", new() { RootPath = root });
            var failure = await Should.ThrowAsync<DatabaseException>(async () =>
                await reopened.OpenDatabaseAsync("LegacyGraph", timeout.Token));
            reopened.TryGetDatabase("LegacyGraph", out _).ShouldBeFalse();
            return failure;
        }
        finally { if (Directory.Exists(root)) { Directory.Delete(root, recursive: true); } }
    }

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
