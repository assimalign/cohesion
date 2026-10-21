using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Graph.Internal;
using Assimalign.Cohesion.Database.Graph.Language;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Graph.Tests;

public sealed class GqlLinearCompositionTests
{
    [Theory(DisplayName = "Cohesion Test [Graph] - Composition: values, filters, fan-out and boundaries share one binding table")]
    [InlineData("LET x = NULL RETURN x", "null")]
    [InlineData("LET x = 1, y = x + 1 RETURN y", "2")]
    [InlineData("FOR x IN [1] INSERT (:P {k:x * 2}) MATCH (p:P) RETURN p.k", "2")]
    [InlineData("LET x = 1 FILTER UNKNOWN RETURN x", "")]
    [InlineData("LET x = 1 FILTER WHERE x = 1 RETURN x", "1")]
    [InlineData("FOR x IN [1,2] RETURN x ORDER BY x", "1,2")]
    [InlineData("UNWIND [1,2] AS x RETURN x ORDER BY x", "1,2")]
    [InlineData("FOR x IN [1,2] WITH x RETURN x ORDER BY x", "1,2")]
    [InlineData("FOR x IN [1,1.0,2] WITH DISTINCT x RETURN x ORDER BY x", "1,2")]
    [InlineData("LET x = 1 RETURN x AS next NEXT RETURN next AS for", "1")]
    [InlineData("LET x = 1 RETURN x + 1 NEXT RETURN 3", "3")]
    [InlineData("FOR x IN NULL RETURN x", "")]
    [InlineData("UNWIND NULL AS x RETURN x", "")]
    [InlineData("LET x = [1,2] FOR y IN x RETURN y ORDER BY y", "1,2")]
    [InlineData("LET l=[[1,2]] FOR x IN l FOR y IN x RETURN y ORDER BY y", "1,2")]
    [InlineData("LET for=1, next=2 RETURN for + next", "3")]
    [InlineData("RETURN 1 AS next NEXT RETURN next", "1")]
    [InlineData("RETURN 1 + 1 NEXT RETURN 3", "3")]
    [InlineData("FOR x IN [3,1,2] WITH x ORDER BY x OFFSET 1 LIMIT 1 WHERE x > 1 RETURN x", "2")]
    [InlineData("FOR x IN [3,1,2] WITH x ORDER BY x LIMIT 1 WHERE x > 1 RETURN x", "")]
    public async Task Execute_Values_ShouldPreserveLinearSemanticsAsync(string source, string expected)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("linear", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Act
        var result = await Rows(session, source);
        // Assert
        string.Join(",", result.Rows.Select(row => row[0] is null ? "null" : Convert.ToString(row[0], System.Globalization.CultureInfo.InvariantCulture))).ShouldBe(expected);
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Composition: scope shrinks and binding kinds are validated per stage")]
    [InlineData("LET x = 1 LET x = 2 RETURN x", "COHDBG003")]
    [InlineData("FOR x IN [1] FOR x IN [2] RETURN x", "COHDBG003")]
    [InlineData("LET x = 1 DELETE x", "COHDBG003")]
    [InlineData("LET x = 1 FILTER x:Foo RETURN x", "COHDBG003")]
    [InlineData("LET x = 1 MATCH (x) RETURN x", "COHDBG003")]
    [InlineData("MATCH (a), (b) WITH a RETURN b", "COHDBG001")]
    [InlineData("MATCH (a), (b) RETURN a NEXT RETURN b", "COHDBG001")]
    [InlineData("MATCH (a) RETURN a.name NEXT RETURN name", "COHDBG001")]
    [InlineData("MATCH (a) WITH a.name RETURN name", "COHDBG001")]
    [InlineData("MATCH (a) DELETE a MATCH (b) RETURN a", "COHDBG003")]
    [InlineData("LET x = 1 WITH x ORDER BY x LET y = missing RETURN y", "COHDBG001")]
    [InlineData("LET x = [1] INSERT (:T {k:x})", "COHDBG003")]
    [InlineData("FOR x IN 1 RETURN x", "COHDBG013")]
    [InlineData("UNWIND 'a' AS x RETURN x", "COHDBG013")]
    public async Task Execute_InvalidBindings_ShouldReportCodedDiagnosticsAsync(string source, string code)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("linear", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(source, cancellationToken: CancellationToken.None));
        // Assert
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(code);
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - FOR: typed lists and null parameters retain elements")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Execute_ForParameters_ShouldExpandListsAndRejectScalarsAsync(bool unwind)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("linear", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        string source = unwind ? "UNWIND $xs AS x RETURN x" : "FOR x IN $xs RETURN x";
        // Act / Assert
        foreach (object? input in new object?[] { new object?[] { 1L, 2L }, new List<long> { 1, 2 }, null })
        {
            var result = await Rows(session, source, new Dictionary<string, object?> { ["xs"] = input });
            result.Rows.Select(row => row[0]).ShouldBe(input is null ? Array.Empty<object?>() : new object?[] { 1L, 2L });
        }
        foreach (object input in new object[] { "text", new byte[] { 1, 2 }, 1L })
        {
            var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(source,
                new Dictionary<string, object?> { ["xs"] = input }, CancellationToken.None));
            error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHDBG013");
        }
        var propertyError = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("INSERT (:T {k:$xs})",
            new Dictionary<string, object?> { ["xs"] = new List<long> { 1, 2 } }, CancellationToken.None));
        propertyError.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHDBG003");
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Composition: insertion labels and own writes are visible to later stages")]
    [InlineData(IsolationLevel.Snapshot, "")]
    [InlineData(IsolationLevel.ReadCommitted, "")]
    [InlineData(IsolationLevel.Snapshot, "NEXT ")]
    [InlineData(IsolationLevel.ReadCommitted, "NEXT ")]
    public async Task Execute_InsertThenMatch_ShouldObserveOwnWritesAsync(IsolationLevel isolation, string next)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("linear", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await using var transaction = await session.BeginTransactionAsync(isolation, CancellationToken.None);
        // Act
        var result = await Rows(session, "INSERT (:T {k:1}) " + next + "MATCH (t:T) RETURN t.k");
        // Assert
        result.Rows.ShouldHaveSingleItem().ShouldBe(new object?[] { 1L });
        await transaction.CommitAsync(CancellationToken.None);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Composition: source-ordered mutations evaluate properties from incoming rows")]
    public async Task Execute_Mutations_ShouldUseRowsAndRespectSourceOrderAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("linear", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Act
        var inserted = await session.ExecuteAsync("FOR name IN $names INSERT (:Person {name:name})",
            new Dictionary<string, object?> { ["names"] = new[] { "Alice", "Bob" } }, CancellationToken.None);
        var result = await Rows(session, "MATCH (a:Person) WITH a WHERE a.name = 'Bob' DELETE a MATCH (b:Person) RETURN b.name");
        await session.ExecuteAsync("MATCH (a:Person) DETACH DELETE a INSERT (:Person {name:'Cara'})", cancellationToken: CancellationToken.None);
        // Assert
        inserted.AffectedCount.ShouldBe(2);
        result.Rows.ShouldHaveSingleItem().ShouldBe(new object?[] { "Alice" });
        (await Rows(session, "MATCH (a:Person) RETURN a.name")).Rows.ShouldHaveSingleItem().ShouldBe(new object?[] { "Cara" });
        (await Rows(session, "MATCH (:Missing) INSERT (:Missing) MATCH (n:Missing) RETURN n")).Rows.ShouldBeEmpty();
        (await Rows(session, "INSERT (:T)-[:Fresh]->(:T) MATCH (a:T)-[r:Fresh]->(b:T) RETURN 1")).Rows.ShouldHaveSingleItem()[0].ShouldBe(1L);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - WITH: aggregation, alias scope, incoming predicates and carried anchors remain stage-local")]
    public async Task Execute_With_ShouldCarryKindsTypesAndGroupingAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("linear", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("INSERT (a:P {k:1})-[:T]->(:P {k:2}), (:P {k:3})", cancellationToken: CancellationToken.None);
        // Act / Assert
        var count = await Rows(session, "MATCH (a:P) WITH count(a) AS c RETURN c");
        count.Types.ShouldBe(new[] { DatabaseType.Int64 });
        count.Rows.ShouldHaveSingleItem().ShouldBe(new object?[] { 3L });
        (await Rows(session, "MATCH (a:P) WITH a.k % 2 AS parity, count(*) AS c RETURN parity, c ORDER BY parity")).Rows.Select(row => row[1]).ShouldBe(new object?[] { 1L, 2L });
        (await Rows(session, "MATCH (a)-[r:T]->(b) WITH a WHERE b.k > 1 RETURN a.k")).Rows.ShouldHaveSingleItem()[0].ShouldBe(1L);
        (await Rows(session, "MATCH (a)-[r:T]->(b) WITH a ORDER BY b.k RETURN a.k")).Rows.ShouldHaveSingleItem()[0].ShouldBe(1L);
        (await Rows(session, "MATCH (a:P) WITH a AS z, a.k AS k MATCH (z)-[:T]->(b) RETURN k, b.k")).Rows.ShouldHaveSingleItem().ShouldBe(new object?[] { 1L, 2L });
        (await Rows(session, "LET x=1, y=2 WITH y,x RETURN *")).Names.ShouldBe(new[] { "y", "x" });
        await using var tx = await session.BeginTransactionAsync(CancellationToken.None);
        const string anchored = "MATCH (a:P) RETURN a AS z NEXT MATCH (b)-[:T]->(z) RETURN b.k";
        var query = GraphQueryRequest.FromGql(anchored).Statement.GqlExpression;
        var plan = new GraphPlanner(database, tx.Context.Snapshot).Plan(query);
        plan.Stages[3].Plan.MatchClauses.Single().Paths.Single().Anchor.NodeIndex.ShouldBe(1);
        plan.Stages[3].Plan.MatchClauses.Single().Paths.Single().Anchor.Property.ShouldBeNull();
        (await Rows(session, anchored)).Rows.ShouldHaveSingleItem().ShouldBe(new object?[] { 1L });
        await tx.RollbackAsync(CancellationToken.None);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Composition: FOR fan-out shares the 100000 binding cap with MATCH")]
    public async Task Execute_Expansion_ShouldFailAtSharedRowCapAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("linear", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("FOR x IN $xs FOR y IN $xs RETURN x, y",
            new Dictionary<string, object?> { ["xs"] = Enumerable.Range(0, 317).ToArray() }, CancellationToken.None));
        // Assert
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHDBG004");
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Composition: EXPLAIN maps every stage and never executes writes")]
    public async Task Execute_Explain_ShouldDescribeAllStageKindsAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("linear", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Act
        var description = await Rows(session, "EXPLAIN LET x=1 FOR y IN [1] UNWIND [2] AS z FILTER TRUE INSERT (a:P) WITH a DELETE a NEXT MATCH (b) RETURN b");
        // Assert
        description.Rows.Where(row => Equals(row[1], 0)).Select(row => row[2]).ShouldBe(new object?[] { "Let", "For", "Unwind", "Filter", "Insert", "With", "Delete", "Next", "Match", "Return" });
        (await Rows(session, "SHOW LABELS")).Rows.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Composition: hand-built stage requests retain catalog and malformed-stage guards")]
    public async Task Execute_HandBuiltStages_ShouldRejectMalformedShapesAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("linear", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        GqlStage[][] malformed = [
            [new GqlInsertStage([])],
            [new GqlInsertStage(null!)],
            [new GqlDeleteStage([])],
            [new GqlDeleteStage(null!)],
            [new GqlReturnStage(null!)],
            [new GqlLetStage(null!), new GqlReturnStage([new GqlProjection(new GqlLiteralExpression(1L))])],
            [new GqlFilterStage(null!), new GqlReturnStage([new GqlProjection(new GqlLiteralExpression(1L))])],
            [new GqlForStage("x", null!), new GqlReturnStage([new GqlProjection(new GqlLiteralExpression(1L))])],
            [new GqlReturnStage([])],
            [new GqlReturnStage([new GqlProjection(new GqlLiteralExpression(1L))]) { Predicate = new GqlLiteralExpression(true) }],
        ];
        // Act / Assert
        foreach (var stages in malformed)
        {
            var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(
                new GraphQueryRequest(new GqlQueryStatement(new GqlQueryExpression(stages))), CancellationToken.None));
            error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHDBG001");
        }
        var show = new GqlQueryExpression([new GqlLetStage([new GqlProjection(new GqlLiteralExpression(1L), "x")])])
        { CatalogSurface = GqlCatalogSurface.Labels };
        var rejected = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(
            new GraphQueryRequest(new GqlQueryStatement(show)), CancellationToken.None));
        rejected.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHDBG001");
        var catalogWrite = new GqlQueryExpression([new GqlInsertStage([])]) { CatalogSurface = GqlCatalogSurface.Labels };
        var writeRejected = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(
            new GraphQueryRequest(new GqlQueryStatement(catalogWrite)), CancellationToken.None));
        writeRejected.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("GQL0007");
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Composition: invalid scopes and duplicate carried names fail during planning, including EXPLAIN")]
    [InlineData("LET x=1 WITH DISTINCT x AS y WHERE x=1 RETURN y", "COHDBG001")]
    [InlineData("MATCH (a:P) WITH DISTINCT a.k AS k WHERE a.k > 1 RETURN k", "COHDBG001")]
    [InlineData("MATCH (a:P) WITH DISTINCT a AS b WHERE a.k > 1 RETURN b", "COHDBG001")]
    [InlineData("MATCH (a:P) WITH a.k AS k, count(*) AS c WHERE a.k > 1 RETURN k", "COHDBG001")]
    [InlineData("MATCH (a:P) WITH a AS b, count(*) AS c WHERE a.k > 1 RETURN b", "COHDBG001")]
    [InlineData("MATCH (a:P) WITH a.k AS k, count(*) AS c WHERE count(*) > 1 RETURN k", "COHDBG001")]
    [InlineData("INSERT (a:T {k: a.k})", "COHDBG001")]
    [InlineData("INSERT (a:T)-[r:R {w:1}]->(b:T {w: r.w})", "COHDBG001")]
    [InlineData("INSERT (a:T)-[r:R {w: r.w}]->(b:T)", "COHDBG001")]
    [InlineData("CREATE (a:T {k: a.k})", "COHDBG001")]
    [InlineData("CREATE (a:T)-[r:R {w:1}]->(b:T {w: r.w})", "COHDBG001")]
    [InlineData("CREATE (a:T)-[r:R {w: r.w}]->(b:T)", "COHDBG001")]
    [InlineData("INSERT (a:T), (b:T {k:a.k})", "COHDBG001")]
    [InlineData("LET x = 1 WITH x, (x) RETURN x", "COHDBG015")]
    [InlineData("MATCH (a) RETURN a, \"a\" NEXT RETURN a", "COHDBG015")]
    [InlineData("LET x = 1 WITH x, \"x\" RETURN x", "COHDBG015")]
    [InlineData("LET x = 1 RETURN x.k", "COHDBG003")]
    [InlineData("FOR x IN [1,2] RETURN x.k", "COHDBG003")]
    [InlineData("LET x=1 RETURN (x).k", "COHDBG003")]
    [InlineData("WITH 1 AS x ORDER BY x.k RETURN x", "COHDBG003")]
    public async Task Plan_InvalidComposition_ShouldRejectBeforeExecutionAsync(string source, string code)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("linear", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await using var transaction = await session.BeginTransactionAsync(CancellationToken.None);
        // Act / Assert
        foreach (string prefix in new[] { "", "EXPLAIN " })
        {
            var query = GraphQueryRequest.FromGql(prefix + source).Statement.GqlExpression;
            var error = Should.Throw<DatabaseException>(() => new GraphPlanner(database, transaction.Context.Snapshot).Plan(query));
            error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(code);
        }
        await transaction.RollbackAsync(CancellationToken.None);
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Composition: map members and quoted star bindings retain their values")]
    [InlineData("LET m={k:1} RETURN m.k", "1")]
    [InlineData("FOR m IN [{k:1}] RETURN m.k", "1")]
    [InlineData("LET m={k:{v:1}} LET n=m.k RETURN n.v", "1")]
    [InlineData("LET m={k:{v:1}} WITH m.k AS n RETURN n.v", "1")]
    [InlineData("LET x=1 WITH x AS y WHERE x=1 RETURN y", "1")]
    [InlineData("LET \"x y\" = 1 WITH * RETURN \"x y\"", "1")]
    [InlineData("LET \"x y\" = 1 RETURN * NEXT RETURN \"x y\"", "1")]
    [InlineData("LET m=NULL RETURN m.k", "null")]
    [InlineData("FOR m IN [] RETURN m.k", "")]
    public async Task Execute_MapAndStarBindings_ShouldPreserveValuesAsync(string source, string expected)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("linear", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Act
        var result = await Rows(session, source);
        // Assert
        string.Join(",", result.Rows.Select(row => row[0] is null ? "null" : Convert.ToString(row[0], System.Globalization.CultureInfo.InvariantCulture))).ShouldBe(expected);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Composition: MATCH maps wait for pattern bindings in either expansion direction")]
    public async Task Execute_PatternProperties_ShouldEvaluateBoundCandidatesAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("linear", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("INSERT (a:P {k:1,name:'Alice'})-[:T {w:1}]->(b:P {k:1,name:'Bob'}), (c:P {k:2,name:'Cara'})-[:T {w:2}]->(:P {k:3,name:'Dan'})", cancellationToken: CancellationToken.None);
        // Act / Assert
        (await Rows(session, "MATCH (n {k:n.k}) RETURN n.name ORDER BY n.name")).Rows.Select(row => row[0]).ShouldBe(new object?[] { "Alice", "Bob", "Cara", "Dan" });
        (await Rows(session, "MATCH (n {k:n.k}) RETURN n")).Rows.Count.ShouldBe(4);
        foreach (string source in new[] {
            "MATCH (b:P) WITH b MATCH (a:P)-[:T]->(b {k:a.k}) RETURN a.name",
            "MATCH (a:P {k:b.k})-[:T]->(b:P) RETURN a.name",
            "MATCH (a:P)-[r:T {w:b.k}]->(b:P) RETURN a.name",
            "MATCH (b:P) WITH b MATCH (a:P {k:b.k})-[:T]->(b) RETURN a.name" })
        { (await Rows(session, source)).Rows.ShouldHaveSingleItem()[0].ShouldBe("Alice"); }
        (await Rows(session, "MATCH (:P {k:b.k})-[:T]->(b:P) RETURN b.name")).Rows.ShouldHaveSingleItem()[0].ShouldBe("Bob");
        (await Rows(session, "MATCH ()-[r:T {w:r.w}]->() RETURN r.w ORDER BY r.w")).Rows.Select(row => row[0]).ShouldBe(new object?[] { 1L, 2L });
        foreach (string boundary in new[] { "WITH *", "RETURN * NEXT" })
        {
            var rows = await Rows(session, "MATCH (\"my node\")->(b) " + boundary + " MATCH (\"my node\")->(c) RETURN c.name ORDER BY c.name");
            rows.Rows.Select(row => row[0]).ShouldBe(new object?[] { "Bob", "Dan" });
        }
        (await Rows(session, "MATCH (a:P) WITH DISTINCT a AS b WHERE b.k=3 RETURN b.name")).Rows.ShouldHaveSingleItem()[0].ShouldBe("Dan");
        (await Rows(session, "MATCH (a:P) WITH a.k AS k, count(*) AS c WHERE c>1 RETURN k")).Rows.ShouldHaveSingleItem()[0].ShouldBe(1L);
        (await Rows(session, "MATCH (a:P) WITH collect(a) AS ns UNWIND ns AS n RETURN n.name ORDER BY n.name")).Rows.Select(row => row[0]).ShouldBe(new object?[] { "Alice", "Bob", "Cara", "Dan" });
        (await Rows(session, "MATCH (a:P) LET m={node:a} LET n=m.node RETURN n.name ORDER BY n.name")).Rows.Select(row => row[0]).ShouldBe(new object?[] { "Alice", "Bob", "Cara", "Dan" });
        var inserted = await session.ExecuteAsync("FOR row IN $rows INSERT (:Imported {name:row.name})", new Dictionary<string, object?> {
            ["rows"] = new object?[] { new Dictionary<string, object?> { ["name"] = "Eve" } } }, CancellationToken.None);
        inserted.AffectedCount.ShouldBe(1);
        (await Rows(session, "MATCH (n:Imported) RETURN n.name")).Rows.ShouldHaveSingleItem()[0].ShouldBe("Eve");
        var invalid = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("FOR m IN [{k:1},2] RETURN m.k", cancellationToken: CancellationToken.None));
        invalid.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHDBG013");
        (await Rows(session, "RETURN 7")).Rows.ShouldHaveSingleItem()[0].ShouldBe(7L);
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - MATCH: every pattern part declares before clause-wide map predicates execute")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Execute_CrossPartProperties_ShouldAgreeWithScansAndWhereAsync(bool indexed)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("linear", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("INSERT (:P {k:1,name:'Alice'})-[:T {w:1}]->(:P {k:1,name:'Bob'}), (:P {k:2,name:'Cara'})-[:T {w:2}]->(:P {k:3,name:'Dan'})", cancellationToken: CancellationToken.None);
        if (indexed) { await GraphSchema.Open(database, session).CreateIndexAsync("P", "by_k", "k", CancellationToken.None); }
        var cases = new (string Source, long Count)[] {
            ("MATCH (a:P {k:b.k}),(b:P)", 6),
            ("MATCH (b:P),(a:P {k:b.k})", 6),
            ("MATCH (a:P),(b:P) WHERE a.k=b.k", 6),
            ("MATCH (:P {k:b.k + 0}),(b:P)", 6),
            ("MATCH (:P {k:b.k,name:'Alice'}),(b:P)", 2),
            ("MATCH (a:P {k:b.k}),(b:P {k:1})", 4),
            ("LET k=1 MATCH (:P {k:b.k}),(b:P {k:k})", 4),
            ("MATCH (a:P {k:b.k}),(b:P {k:$k})", 4),
            ("MATCH (:P {k:c.k})->(b:P),(c:P)", 3),
            ("MATCH (b:P) WITH b MATCH (:P {k:c.k})->(b),(c:P)", 3),
            ("MATCH ()-[r:T {w:b.k}]->(),(b:P)", 3),
            ("MATCH ()-[:T {w:b.k}]->(),(b:P)", 3),
            ("MATCH (a:P {k:c.k}),(b:P {k:a.k}),(c:P {k:b.k})", 10),
            ("MATCH (a:P {k:99}) OPTIONAL MATCH (:P {k:b.k}),(b:P)", 0),
            ("OPTIONAL MATCH (:P {k:b.k}),(b:P {k:99})", 1),
        };
        // Act / Assert
        foreach (var item in cases)
        {
            string source = item.Source + " RETURN count(*) AS c";
            var parameters = new Dictionary<string, object?> { ["k"] = 1L };
            (await Rows(session, source, parameters)).Rows.ShouldHaveSingleItem().ShouldBe(new object?[] { item.Count });
            (await Rows(session, "EXPLAIN " + source, parameters)).Rows.ShouldNotBeEmpty();
        }
        foreach (string prefix in new[] { "", "EXPLAIN " })
        {
            var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(
                prefix + "MATCH (a:P {k:b.k}) MATCH (b:P) RETURN a.name", cancellationToken: CancellationToken.None));
            error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHDBG001");
        }
        (await Rows(session, "RETURN 7")).Rows.ShouldHaveSingleItem()[0].ShouldBe(7L);
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - FOR and UNWIND: proven entity elements retain kinds through WITH and NEXT")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Execute_EntityLists_ShouldCarryKindsIntoMatchAndDeleteAsync(bool unwind)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("linear", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("INSERT (:P {name:'Alice'})-[:T {name:'AB'}]->(:P {name:'Bob'}), (:P {name:'Cara'})-[:T {name:'CD'}]->(:P {name:'Dan'})", cancellationToken: CancellationToken.None);
        string loop = unwind ? "UNWIND ns AS n" : "FOR n IN ns";
        // Act / Assert
        foreach (string list in new[] { "WITH collect(a) AS ns", "WITH [a] AS ns", "LET ns=[a] WITH ns", "WITH [[a]] AS nested FOR ns IN nested" })
        {
            foreach (string boundary in new[] { "", "WITH n", "RETURN n NEXT", "WITH n AS carried WITH carried AS n" })
            {
                string source = "MATCH (a:P) " + list + " " + loop + " " + boundary + " MATCH (n) RETURN n.name ORDER BY n.name";
                (await Rows(session, source)).Rows.Select(row => row[0]).ShouldBe(new object?[] { "Alice", "Bob", "Cara", "Dan" });
                (await Rows(session, "EXPLAIN " + source)).Rows.ShouldNotBeEmpty();
            }
        }
        foreach (string boundary in new[] { "WITH ns", "RETURN ns NEXT" })
        {
            string source = "MATCH (a:P) WITH collect(a) AS ns " + boundary + " " + loop + " MATCH (n) RETURN n.name ORDER BY n.name";
            (await Rows(session, source)).Rows.Select(row => row[0]).ShouldBe(new object?[] { "Alice", "Bob", "Cara", "Dan" });
        }
        foreach (string list in new[] { "collect(r)", "[r]", "CASE WHEN TRUE THEN [r] ELSE [r] END" })
        {
            foreach (string boundary in new[] { "WITH n", "RETURN n NEXT" })
            {
                string source = "MATCH ()-[r:T]->() WITH " + list + " AS ns " + loop + " " + boundary + " MATCH ()-[n]->() RETURN n.name ORDER BY n.name";
                (await Rows(session, source)).Rows.Select(row => row[0]).ShouldBe(new object?[] { "AB", "CD" });
                (await Rows(session, "EXPLAIN " + source)).Rows.ShouldNotBeEmpty();
            }
        }
        string deleteEdges = "MATCH ()-[r:T]->() WITH collect(r) AS ns " + loop + " RETURN n NEXT DELETE n";
        string deleteNodes = "MATCH (a:P) WITH collect(a) AS ns " + loop + " WITH n DETACH DELETE n";
        (await Rows(session, "EXPLAIN " + deleteEdges)).Rows.ShouldNotBeEmpty();
        (await Rows(session, "EXPLAIN " + deleteNodes)).Rows.ShouldNotBeEmpty();
        (await Rows(session, "MATCH (a:P) RETURN count(*)")).Rows.ShouldHaveSingleItem()[0].ShouldBe(4L);
        await using (var transaction = await session.BeginTransactionAsync(CancellationToken.None))
        {
            (await session.ExecuteAsync(deleteNodes, cancellationToken: CancellationToken.None)).AffectedCount.ShouldBe(4);
            (await Rows(session, "MATCH ()-[r:T]->() RETURN count(*)")).Rows.ShouldHaveSingleItem()[0].ShouldBe(0L);
            await transaction.RollbackAsync(CancellationToken.None);
        }
        (await session.ExecuteAsync(deleteEdges, cancellationToken: CancellationToken.None)).AffectedCount.ShouldBe(2);
        (await session.ExecuteAsync(deleteNodes, cancellationToken: CancellationToken.None)).AffectedCount.ShouldBe(4);
        (await Rows(session, "MATCH (a:P) RETURN count(*)")).Rows.ShouldHaveSingleItem()[0].ShouldBe(0L);
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - FOR: mixed or scalar elements keep Value binding rejections")]
    [InlineData("UNWIND [1] AS n MATCH (n) RETURN n", "COHDBG003")]
    [InlineData("FOR n IN [1] WITH n DETACH DELETE n", "COHDBG003")]
    [InlineData("MATCH (a:P) WITH [a,1] AS ns UNWIND ns AS n MATCH (n) RETURN n.name", "COHDBG003")]
    [InlineData("MATCH (a:P) WITH CASE WHEN TRUE THEN [a] ELSE [1] END AS ns FOR n IN ns MATCH (n) RETURN 1", "COHDBG003")]
    [InlineData("MATCH (a:P) WITH COALESCE([a],[1]) AS ns FOR n IN ns DELETE n", "COHDBG003")]
    [InlineData("MATCH (a:P)-[r:T]->() WITH [a,r] AS ns FOR n IN ns DELETE n", "COHDBG003")]
    [InlineData("MATCH (a:P) WITH collect(a) AS ns UNWIND ns AS n MATCH ()-[n]->() RETURN 1", "COHDBG003")]
    [InlineData("MATCH ()-[r:T]->() WITH collect(r) AS ns FOR n IN ns MATCH (n) RETURN 1", "COHDBG003")]
    [InlineData("FOR n IN $ns RETURN n NEXT MATCH (n) RETURN n.name", "COHDBG003")]
    public async Task Execute_UnprovenEntityElements_ShouldRejectDuringPlanningAsync(string source, string code)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("linear", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Act / Assert
        foreach (string prefix in new[] { "", "EXPLAIN " })
        {
            var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(prefix + source,
                new Dictionary<string, object?> { ["ns"] = new object?[] { 1L } }, CancellationToken.None));
            error.Diagnostics[0].Code.ShouldBe(code);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - FOR: nullable entity elements retain optional path rejection")]
    public async Task Execute_NullableEntityElements_ShouldPreserveNullAndPathRulesAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("linear", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("INSERT (:P)", cancellationToken: CancellationToken.None);
        const string source = "MATCH (a:P) WITH [a,NULL] AS ns FOR n IN ns WITH n";
        // Act / Assert
        (await Rows(session, source + " MATCH (n) RETURN count(*)")).Rows.ShouldHaveSingleItem()[0].ShouldBe(1L);
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(
            GraphPathsQueryRequest.FromGql(source + " RETURN n"), CancellationToken.None));
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHDBG001");
        (await session.ExecuteAsync(source + " DETACH DELETE n", cancellationToken: CancellationToken.None)).AffectedCount.ShouldBe(1);
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Carried runtime entities: MATCH and DELETE report coded kind errors before mutation")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Execute_WrongRuntimeEntityKind_ShouldFailWithValueDiagnosticAsync(bool relationship)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("linear", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("INSERT (:P)-[:T]->(:P)", cancellationToken: CancellationToken.None);
        var node = (await Rows(session, "MATCH (a:P) RETURN a")).Rows[0][0];
        var edge = (await Rows(session, "MATCH ()-[r:T]->() RETURN r")).Rows[0][0];
        // Act / Assert
        await database.RunAsync(session, async operation =>
        {
            string source = relationship ? "MATCH ()-[n]->() RETURN 1" : "MATCH (n) RETURN 1";
            var plan = new GraphPlanner(database, operation.Context.Snapshot).Plan(GraphQueryRequest.FromGql(source).Statement.GqlExpression);
            foreach (var value in new[] { (object?)1L, relationship ? node : edge })
            {
                var budget = new GraphPlanExecutor.ExpansionBudget();
                var error = await Should.ThrowAsync<DatabaseException>(async () => await GraphPlanExecutor.MatchAsync(database, operation,
                    plan.MatchClauses[0].Paths[0], new(StringComparer.Ordinal) { ["n"] = value }, [], budget, null, CancellationToken.None));
                error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHDBG013");
                budget.Consumed.ShouldBe(0);
            }
            var deletion = await Should.ThrowAsync<DatabaseException>(async () => await GraphPlanExecutor.DeleteAsync(database, operation,
                new GqlDeleteStage(["n"]) { Detach = true }, [new(StringComparer.Ordinal) { ["n"] = node }, new(StringComparer.Ordinal) { ["n"] = 1L }], CancellationToken.None));
            deletion.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHDBG013");
            return true;
        }, CancellationToken.None);
        (await Rows(session, "MATCH (a:P) RETURN count(*)")).Rows.ShouldHaveSingleItem()[0].ShouldBe(2L);
        (await Rows(session, "RETURN 7")).Rows.ShouldHaveSingleItem()[0].ShouldBe(7L);
    }

    private static async Task<(string[] Names, DatabaseType[] Types, List<object?[]> Rows)> Rows(GraphDatabaseSession session,
        string source, IReadOnlyDictionary<string, object?>? parameters = null)
    {
        await using var result = (await session.ExecuteAsync(source, parameters, CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();
        var rows = new List<object?[]>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { rows.Add(Enumerable.Range(0, row.FieldCount).Select(row.GetValue).ToArray()); }
        return (result.Columns.Select(column => column.Name).ToArray(), result.Columns.Select(column => column.Type).ToArray(), rows);
    }
}
