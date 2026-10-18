using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Graph.Catalog;
using Assimalign.Cohesion.Database.Graph.Internal;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Graph.Tests;

public sealed class GraphUpdateTests
{
    [Theory(DisplayName = "Cohesion Test [Graph] - SET/REMOVE: properties, maps, labels and NULL execute (#1149)")]
    [InlineData("SET a.n = a.n + 1", 2L, true, true)]
    [InlineData("SET a.n = NULL", null, true, true)]
    [InlineData("REMOVE a.n", null, true, true)]
    [InlineData("SET a = {n:5}", 5L, false, true)]
    [InlineData("SET a += {n:5, temp:NULL}", 5L, false, true)]
    [InlineData("SET a:Admin:Graph", 1L, true, true)]
    [InlineData("REMOVE a IS P", 1L, true, false)]
    public async Task Execute_Updates_ShouldApplyResultingEntity(string update, object? number, bool temp, bool label)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("updates", new());
        var db = await engine.CreateDatabaseAsync("graph");
        await using var session = await db.CreateSessionAsync();
        await session.ExecuteAsync("INSERT (:P {n:1,temp:NULL})");

        // Act
        var result = await session.ExecuteAsync($"MATCH (a:P) {update} RETURN a.n, keys(a), labels(a)");
        var rows = await Read(result);

        // Assert
        result.AffectedCount.ShouldBe(1);
        rows.Single()[0].ShouldBe(number);
        rows[0][1].ShouldBeOfType<object?[]>().Contains("temp").ShouldBe(temp);
        rows[0][2].ShouldBeOfType<object?[]>().Contains("P").ShouldBe(label);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - SET: repeated rows accumulate, stages publish one replacement and refresh composite aliases (#1149)")]
    public async Task Execute_RepeatedEntity_ShouldAccumulateAndRefreshEveryBinding()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("updates", new());
        var db = await engine.CreateDatabaseAsync("graph");
        await using var session = await db.CreateSessionAsync();
        await session.ExecuteAsync("INSERT (a:P {n:0})-[r:R {x:0}]->(b:P {n:0})");
        await using var pinSession = await db.CreateSessionAsync();
        await using var pin = await pinSession.BeginTransactionAsync(IsolationLevel.Snapshot);

        // Act
        var result = await session.ExecuteAsync("MATCH p=(a)-[r:R]->(b) LET alias=a, nested={v:[a,r,p]} FOR v IN [1,2,3] SET a.n=a.n+1, r.x=r.x+1 SET a.n=a.n+1 RETURN alias.n, nested, endNode(r).n, a.n");
        var rows = await Read(result);

        // Assert
        result.AffectedCount.ShouldBe(2);
        rows.Count.ShouldBe(3);
        rows.ShouldAllBe(row => Equals(row[0], 6L) && Equals(row[2], 0L) && Equals(row[3], 6L));
        var nested = rows[0][1].ShouldBeAssignableTo<IReadOnlyDictionary<string, object?>>()!["v"].ShouldBeOfType<object?[]>();
        nested[0].ShouldBeOfType<GraphNode>().Properties["n"].ShouldBe(6L);
        nested[1].ShouldBeOfType<GraphRelationship>().Properties["x"].ShouldBe(3L);
        nested[2].ShouldBeOfType<GraphPath>().Nodes[0].Properties["n"].ShouldBe(6L);
        var counts = new Dictionary<ulong, int>();
        using var units = db.DataStorage.GetUnitIterator(2);
        while (units.MoveNext())
        {
            var bytes = units.Current.Data;
            if (bytes.Span[16] is not (1 or 2)) { continue; }
            ulong id = BinaryPrimitives.ReadUInt64LittleEndian(bytes.Span.Slice(18, 8));
            counts[id] = counts.GetValueOrDefault(id) + 1;
        }
        counts.Values.Order().ShouldBe([1, 2, 2]);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - SET: NEXT, FOR parameters, new labels and insertion compose (#1149)")]
    public async Task Execute_ComposedUpdates_ShouldSeeWorkingCopiesAndCountDistinctEntities()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("updates", new());
        var db = await engine.CreateDatabaseAsync("graph");
        await using var session = await db.CreateSessionAsync();
        await session.ExecuteAsync("INSERT (:P {k:1}), (:P {k:2})");

        // Act / Assert
        var result = await session.ExecuteAsync("MATCH (a:P) SET a.n=1, a IS Graph NEXT MATCH (b:Graph) RETURN b.n");
        (await Read(result)).Select(row => row[0]).ShouldBe([1L, 1L]);
        result.AffectedCount.ShouldBe(2);
        (await session.ExecuteAsync("FOR v IN $vs MATCH (a:P {k:v}) SET a.seen=TRUE", new Dictionary<string, object?> { ["vs"] = new long[] { 1, 2 } })).AffectedCount.ShouldBe(2);
        var inserted = await session.ExecuteAsync("INSERT (n:T {k:1}) SET n.v=2 RETURN n.v");
        (await Read(inserted)).Single()[0].ShouldBe(2L);
        inserted.AffectedCount.ShouldBe(1);
        (await session.ExecuteAsync("MATCH (a:P) SET a.n=1, a:Graph REMOVE a.missing")).AffectedCount.ShouldBe(0);
        (await session.ExecuteAsync("OPTIONAL MATCH (a:Absent) SET a.x=1 REMOVE a:Graph")).AffectedCount.ShouldBe(0);
        (await Read(await session.ExecuteAsync("SHOW PROPERTY KEYS"))).ShouldContain(row => row.Contains("seen"));
        (await Read(await session.ExecuteAsync("SHOW LABELS"))).ShouldContain(row => row.Contains("Graph"));
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - SET: type-exact no-ops and left-to-right items (#1149)")]
    public async Task Execute_ExactTypesAndOrdering_ShouldCountOnlyChangedEntities()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("updates", new());
        var db = await engine.CreateDatabaseAsync("graph");
        await using var session = await db.CreateSessionAsync();
        await session.ExecuteAsync("INSERT (:P {x:1})");

        // Act / Assert
        (await session.ExecuteAsync("MATCH (a:P) SET a.x=1")).AffectedCount.ShouldBe(0);
        (await session.ExecuteAsync("MATCH (a:P) SET a.x=1.0")).AffectedCount.ShouldBe(1);
        (await Read(await session.ExecuteAsync("MATCH (a:P) SET a={x:1}, a.y=2 RETURN keys(a)"))).Single()[0].ShouldBeOfType<object?[]>().ShouldBe(["x", "y"]);
        (await Read(await session.ExecuteAsync("MATCH (a:P) SET a.y=2, a={x:1} RETURN keys(a)"))).Single()[0].ShouldBeOfType<object?[]>().ShouldBe(["x"]);
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - SET/REMOVE: invalid targets and values fail before publication (#1149)")]
    [InlineData("MATCH p=(a) SET p.x=1")]
    [InlineData("MATCH p=(a) REMOVE p.x")]
    [InlineData("MATCH (a)-[r:R]->{1,2}(b) SET r.x=1")]
    [InlineData("LET x=1 SET x.y=1")]
    [InlineData("FOR v IN $vs SET v.x=1")]
    [InlineData("MATCH ()-[r:R]->() SET r:X")]
    [InlineData("MATCH ()-[r:R]->() REMOVE r:X")]
    [InlineData("MATCH (a) LET x=[1] SET a.x=x")]
    public async Task Execute_InvalidUpdate_ShouldReportKindError(string query)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("updates", new());
        var db = await engine.CreateDatabaseAsync("graph");
        await using var session = await db.CreateSessionAsync();
        await session.ExecuteAsync("INSERT (:P)-[:R]->(:P)");

        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(query,
            new Dictionary<string, object?> { ["vs"] = new[] { 1 } }));

        // Assert
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHDBG003");
        (await Read(await session.ExecuteAsync("MATCH (a:P) RETURN a.x"))).ShouldAllBe(row => row[0] == null);
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - SET/REMOVE: required keys and declared types reuse INSERT validation (#1149)")]
    [InlineData("REMOVE a.required")]
    [InlineData("SET a.required=NULL")]
    [InlineData("SET a.required='wrong'")]
    [InlineData("SET a:Required")]
    public async Task Execute_SchemaViolation_ShouldRollback(string update)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("updates", new());
        var db = await engine.CreateDatabaseAsync("graph");
        await using var session = await db.CreateSessionAsync();
        var schema = GraphSchema.Open(db, session);
        var label = new GraphLabelMetadata(Guid.NewGuid(), "P");
        var required = new GraphLabelMetadata(Guid.NewGuid(), "Required");
        await schema.SaveLabelAsync(label);
        await schema.SaveLabelAsync(required);
        await schema.SavePropertyKeyAsync(new(label.Id, "required", DatabaseType.Int64, Required: true));
        await schema.SavePropertyKeyAsync(new(required.Id, "other", DatabaseType.String, Required: true));
        await session.ExecuteAsync("INSERT (:P {required:1})");

        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync($"MATCH (a:P) {update}"));

        // Assert
        error.Message.ShouldContain("COHDBG003", Case.Sensitive);
        (await Read(await session.ExecuteAsync("MATCH (a:P) RETURN a.required"))).Single()[0].ShouldBe(1L);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - SET/REMOVE: EXPLAIN describes writes and ExecutePaths refuses them (#1149)")]
    public async Task Explain_Updates_ShouldDescribeWithoutExecuting()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("updates", new());
        var db = await engine.CreateDatabaseAsync("graph");
        await using var session = await db.CreateSessionAsync();
        await session.ExecuteAsync("INSERT (:P {x:1})");

        // Act / Assert
        foreach (string update in new[] { "SET a.x=2", "REMOVE a.x" })
        {
            var rows = await Read(await session.ExecuteAsync($"EXPLAIN MATCH (a:P) {update}"));
            rows.ShouldContain(row => row.Contains(update.StartsWith("SET", StringComparison.Ordinal) ? "Set" : "Remove"));
            rows.ShouldContain(row => row.Contains("UpdateEntity"));
            await Should.ThrowAsync<DatabaseException>(async () =>
            {
                await session.ExecuteAsync(GraphPathsQueryRequest.FromGql($"MATCH (a:P) {update} RETURN a"));
            });
        }
        (await Read(await session.ExecuteAsync("MATCH (a:P) RETURN a.x"))).Single()[0].ShouldBe(1L);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - SET: indexed matches update once and adjacency exposes replacements from both endpoints (#1149)")]
    public async Task Execute_IndexedNodesAndRelationships_ShouldMaintainEveryAccessPath()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("updates", new());
        var db = await engine.CreateDatabaseAsync("graph");
        await using var session = await db.CreateSessionAsync();
        await session.ExecuteAsync("INSERT (a:P {age:18})-[r:R {weight:1}]->(b:P {age:18}), (a)-[:R {weight:1}]->(a)");
        await GraphSchema.Open(db, session).CreateIndexAsync("P", "by_age", "age");

        // Act / Assert
        (await session.ExecuteAsync("MATCH (a:P) WHERE a.age=18 SET a.age=a.age+1")).AffectedCount.ShouldBe(2);
        (await Read(await session.ExecuteAsync("MATCH (a:P {age:19}) RETURN a.age"))).Count.ShouldBe(2);
        (await session.ExecuteAsync("MATCH ()-[r:R]->() SET r.weight=9")).AffectedCount.ShouldBe(2);
        var outgoing = await Read(await session.ExecuteAsync("MATCH (a)-[r:R]->(b) RETURN r.weight"));
        var incoming = await Read(await session.ExecuteAsync("MATCH (b)<-[r:R]-(a) RETURN r.weight"));
        outgoing.Count.ShouldBe(2);
        incoming.Count.ShouldBe(2);
        outgoing.ShouldAllBe(row => Equals(row[0], 9L));
        incoming.ShouldAllBe(row => Equals(row[0], 9L));
        await session.ExecuteAsync("MATCH (a:P) DETACH DELETE a");
        (await Read(await session.ExecuteAsync("MATCH (a:P {age:19}) RETURN a"))).ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - SET: schema-owned definitions retain their ownership and closed key metadata (#1149)")]
    public async Task Execute_SchemaOwnedLabel_ShouldStoreValuesWithoutDiscoveringKeys()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("updates", new());
        var db = await engine.CreateDatabaseAsync("graph");
        await using var session = await db.CreateSessionAsync();
        var schema = GraphSchema.Open(db, session);
        Guid id = Guid.NewGuid();
        await schema.SaveLabelAsync(new(id, "Managed", DatabaseObjectOwner.Schema, "Application"));
        await session.ExecuteAsync("INSERT (:Managed)");
        var ownership = await Read(await session.ExecuteAsync("SHOW OBJECT OWNERSHIP"));

        // Act
        (await session.ExecuteAsync("MATCH (a:Managed) SET a.discovered=1")).AffectedCount.ShouldBe(1);

        // Assert
        (await schema.GetPropertyKeysAsync(id)).ShouldBeEmpty();
        (await Read(await session.ExecuteAsync("MATCH (a:Managed) RETURN a.discovered"))).Single()[0].ShouldBe(1L);
        var after = await Read(await session.ExecuteAsync("SHOW OBJECT OWNERSHIP"));
        after.Count.ShouldBe(ownership.Count);
        for (int i = 0; i < after.Count; i++) { after[i].ShouldBe(ownership[i]); }
    }

    private static async Task<List<object?[]>> Read(DatabaseResult result)
    {
        await using var rows = result.ShouldBeAssignableTo<DatabaseResultSet>();
        var values = new List<object?[]>();
        await foreach (var row in rows.GetRowsAsync(CancellationToken.None))
        { values.Add(Enumerable.Range(0, row.FieldCount).Select(row.GetValue).ToArray()); }
        return values;
    }
}
