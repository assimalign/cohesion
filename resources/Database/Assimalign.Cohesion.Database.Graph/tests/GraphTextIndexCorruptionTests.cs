using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Graph.Internal;
using Assimalign.Cohesion.Database.Graph.Storage;

namespace Assimalign.Cohesion.Database.Graph.Tests;

public sealed class GraphTextIndexCorruptionTests
{
    [Theory(DisplayName = "Cohesion Test [Graph] - Text index corruption gates refuse images without changing files (#1644)")]
    [InlineData("fixed-receipt")] [InlineData("live-receipt")]
    [InlineData("kind")] [InlineData("revision")]
    public async Task OpenDatabaseAsync_MalformedIndexImage_ShouldRefuseWithoutWritesAsync(string mutation)
    {
        // Arrange: mutate a real, cleanly closed image in a journaled physical bracket.
        string root = Path.Combine(Path.GetTempPath(), "cohesion-text-corrupt-" + Guid.NewGuid().ToString("N"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            await using (var engine = GraphDatabaseEngine.Create("corrupt", new() { RootPath = root }))
            {
                var database = await engine.CreateDatabaseAsync("graph", timeout.Token);
                await using var session = await database.CreateSessionAsync(timeout.Token);
                await session.ExecuteAsync("INSERT (:N {p:'abcdef'})", cancellationToken: timeout.Token);
                await session.ExecuteAsync("INSERT (:Start)-[:R]->(:End)", cancellationToken: timeout.Token);
                await session.ExecuteAsync("CREATE TEXT INDEX t FOR (n:N) ON (n.p)", cancellationToken: timeout.Token);
            }
            string directory = Path.Combine(root, "graph");
            using (var storage = GraphStorage.Open(File.Open(Path.Combine(directory, "graph.dat"), FileMode.Open, FileAccess.ReadWrite),
                File.Open(Path.Combine(directory, "graph.log"), FileMode.Open, FileAccess.ReadWrite),
                File.Open(Path.Combine(directory, "graph.bak"), FileMode.Open, FileAccess.ReadWrite), checkpointOnOpen: false))
            {
                ulong propertyOwner;
                using (var definitions = storage.GetUnitIterator(2))
                {
                    do { definitions.MoveNext().ShouldBeTrue(); } while (definitions.Current.Data.Span[16] != 3);
                    propertyOwner = BinaryPrimitives.ReadUInt64LittleEndian(definitions.Current.Data.Span[18..]);
                }
                ulong owner = mutation is "kind" ? 0UL : mutation is "revision" ? 2UL : 1UL;
                using var records = storage.GetUnitIterator(owner);
                bool changed = false;
                while (records.MoveNext())
                {
                    var record = records.Current;
                    byte[] bytes = record.Data.ToArray();
                    if (mutation == "kind") { if (bytes[16] != 3) { continue; } bytes[^1] = 2; }
                    else if (mutation == "revision")
                    { if (bytes[16] != 3) { continue; } BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(26), 2); }
                    else
                    {
                        ulong id = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(18));
                        // Adjacency has no secondary required-label-tree gate: this isolates the fixed-owner receipt check.
                        if (id != (mutation == "live-receipt" ? propertyOwner : GraphLockNamespaces.Adjacency)) { continue; }
                        long page = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(26));
                        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(26), -page);
                    }
                    using var bracket = storage.BeginTransaction();
                    storage.UpdateEntry(bracket, record.PageId, record.SlotIndex, bytes);
                    bracket.Commit();
                    changed = true;
                    break;
                }
                changed.ShouldBeTrue();
            }
            var before = Directory.GetFiles(directory).ToDictionary(path => path, File.ReadAllBytes);

            // Act
            await using var reopened = GraphDatabaseEngine.Create("corrupt", new() { RootPath = root });
            var error = await Should.ThrowAsync<DatabaseException>(async () => await reopened.OpenDatabaseAsync("graph", timeout.Token));

            // Assert: existing corruption identities, including the area's shared damage signal.
            error.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == (mutation == "kind" ? "CG06006" : "CG06018"));
            reopened.TryGetDatabase("graph", out _).ShouldBeFalse();
            await reopened.DisposeAsync();
            Directory.GetFiles(directory).Order().ShouldBe(before.Keys.Order());
            foreach (var (path, bytes) in before) { File.ReadAllBytes(path).ShouldBe(bytes, path); }
        }
        finally { if (Directory.Exists(root)) { Directory.Delete(root, recursive: true); } }
    }
}
