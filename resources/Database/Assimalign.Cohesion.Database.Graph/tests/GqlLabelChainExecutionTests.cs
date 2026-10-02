using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Graph.Language;

namespace Assimalign.Cohesion.Database.Graph.Tests;

/// <summary>
/// Label expressions and predicates have no fixed length or nesting limit (#1139 follow-up, owner
/// decision 2026-10-02: "do what Neo4j does"). 10,000-name chains of every operator plan and run in
/// node patterns, edge patterns, labeled predicates and insertions; flattening keeps
/// <c>!</c> &gt; <c>&amp;</c> &gt; <c>|</c>; and a statement nested deeper than the executing thread's
/// stack fails with <c>COHDBG007</c> while the session keeps serving.
/// </summary>
public sealed class GqlLabelChainExecutionTests
{
    private const int chainLength = 10_000;

    // 200 distinct labels, so a node carrying all of them fits one graph record; a 10,000-name chain
    // cycles through them 50 times.
    private static readonly string[] labels = Enumerable.Range(0, 200).Select(i => "L" + i.ToString(CultureInfo.InvariantCulture)).ToArray();
    private static readonly string[] types = Enumerable.Range(0, 200).Select(i => "T" + i.ToString(CultureInfo.InvariantCulture)).ToArray();

    /// <param name="gql">A read whose chain is at <c>{0}</c> (labels) or <c>{1}</c> (types).</param>
    /// <param name="separator">The chain's operator.</param>
    /// <param name="expected">The comma-separated names it returns, in identity order.</param>
    [Theory(DisplayName = "Cohesion Test [Graph] - Label chains: 10,000-name chains of each operator select exactly the expected rows")]
    [InlineData("MATCH (n:{0}) RETURN n.name", "|", "all,even")]
    [InlineData("MATCH (n:{0}) RETURN n.name", "&", "all")]
    [InlineData("MATCH (n:{0}) RETURN n.name", ":", "all")]
    [InlineData("MATCH (n IS {0}) RETURN n.name", "&", "all")]
    [InlineData("MATCH (n) WHERE n:{0} RETURN n.name", "|", "all,even")]
    [InlineData("MATCH (n) WHERE n IS LABELED {0} RETURN n.name", "&", "all")]
    [InlineData("MATCH (n) WHERE n IS NOT LABELED {0} RETURN n.name", "|", "x,u")]
    [InlineData("MATCH (n) WHERE n.name <> 'none' AND n:{0} AND n.name <> 'even' RETURN n.name", "|", "all")]
    [InlineData("MATCH (a)-[r:{1}]->(b) WHERE r.name = 't0' RETURN r.name", "|", "t0")]
    [InlineData("MATCH (a)-[r IS {1}]->(b) RETURN r.name", "&", "")]
    [InlineData("MATCH ()-[r]->() WHERE r IS LABELED {1} RETURN r.name", "&", "")]
    public async Task Execute_TenThousandNameChain_ShouldSelectExactRowsAsync(string gql, string separator, string expected)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create(new());
        await using var session = await SeedAsync(engine);
        gql = string.Format(CultureInfo.InvariantCulture, gql, Chain(labels, separator), Chain(types, separator));

        // Act
        var names = await ColumnAsync(session, gql);

        // Assert
        names.ShouldBe(expected.Length == 0 ? [] : expected.Split(','));
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Label chains: a 10,000-name disjunction over relationship types matches every typed edge")]
    public async Task Execute_TenThousandTypeDisjunction_ShouldMatchEveryEdgeAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create(new());
        await using var session = await SeedAsync(engine);

        // Act
        var names = await ColumnAsync(session, "MATCH (a)-[r:" + Chain(types, "|") + "]->(b) RETURN r.name");
        var single = await ColumnAsync(session, "MATCH (a)-[r:" + string.Join("&", Enumerable.Repeat("T7", chainLength)) + "]->(b) RETURN r.name");
        var predicate = await ColumnAsync(session, "MATCH ()-[r]->() WHERE r:" + Chain(types, "|") + " RETURN r.name");

        // Assert
        names.ShouldBe(types.Select(type => type.ToLowerInvariant()));
        single.ShouldBe(["t7"]);
        predicate.ShouldBe(names);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Label chains: 10,000 labeled predicates joined by AND filter as one chain")]
    public async Task Execute_TenThousandLabeledPredicates_ShouldFilterAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create(new());
        await using var session = await SeedAsync(engine);
        string every = string.Join(" AND ", Enumerable.Range(0, chainLength).Select(i => "n:" + labels[i % labels.Length]));
        string comparisons = string.Join(" AND ", Enumerable.Range(0, chainLength).Select(_ => "n.name <> 'x'"));

        // Act
        var all = await ColumnAsync(session, "MATCH (n) WHERE " + every + " RETURN n.name");
        var notX = await ColumnAsync(session, "MATCH (n) WHERE " + comparisons + " RETURN n.name");

        // Assert
        all.ShouldBe(["all"]);
        notX.ShouldBe(["all", "even", "u"]);
    }

    /// <summary>
    /// A chain over many distinct catalog labels resolves each once and matches every node that
    /// carries any of them. The distinct count is 500 rather than 10,000 because creating labels
    /// costs time linear in the labels already defined (a catalog limit recorded in the Graph
    /// design), not because of any limit on the expression.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Graph] - Label chains: a 10,000-name chain over 500 distinct labels resolves and matches")]
    public async Task Execute_ChainOverManyDistinctLabels_ShouldMatchAsync()
    {
        // Arrange: ten nodes, each carrying 50 of D0..D499.
        await using var engine = GraphDatabaseEngine.Create(new());
        var database = (IGraphDatabase)await engine.CreateDatabaseAsync("distinct", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        string[] names = Enumerable.Range(0, 500).Select(i => "D" + i.ToString(CultureInfo.InvariantCulture)).ToArray();
        string insert = "INSERT " + string.Join(", ", names.Chunk(50).Select((group, index) =>
            "(:" + string.Join("&", group) + " {g: " + index.ToString(CultureInfo.InvariantCulture) + "})"));
        await session.ExecuteAsync(insert, cancellationToken: CancellationToken.None);

        // Act
        var any = await RowsAsync(session, "MATCH (n:" + Chain(names, "|") + ") RETURN n.g");
        var labeled = await RowsAsync(session, "MATCH (n) WHERE n IS LABELED " + Chain(names, "|") + " RETURN n.g");
        var every = await RowsAsync(session, "MATCH (n:" + Chain(names, "&") + ") RETURN n.g");
        var first = await RowsAsync(session, "MATCH (n:" + Chain(names.Take(50).ToArray(), "&") + ") RETURN n.g");
        var catalog = await RowsAsync(session, "SHOW LABELS");

        // Assert
        any.Select(row => row[0]).ShouldBe(Enumerable.Range(0, 10).Select(index => (object?)(long)index));
        labeled.Select(row => row[0]).ShouldBe(any.Select(row => row[0]));
        every.ShouldBeEmpty();
        first.ShouldHaveSingleItem()[0].ShouldBe(0L);
        catalog.Count.ShouldBe(names.Length);
    }

    /// <param name="separator">The conjunction spelling: <c>&amp;</c> or the repeated <c>:</c>.</param>
    [Theory(DisplayName = "Cohesion Test [Graph] - Label chains: a 10,000-name INSERT conjunction creates one node, each label once")]
    [InlineData("&")]
    [InlineData(":")]
    public async Task Execute_TenThousandNameInsertConjunction_ShouldCreateOneNodeAsync(string separator)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create(new());
        await using var session = await SeedAsync(engine);
        int before = (await RowsAsync(session, "MATCH (n) RETURN n")).Count;

        // Act
        var result = await session.ExecuteAsync("INSERT (n:" + Chain(labels, separator) + " {name: 'new'})", cancellationToken: CancellationToken.None);
        var created = await RowsAsync(session, "MATCH (n {name: 'new'}) RETURN n");

        // Assert
        result.AffectedCount.ShouldBe(1);
        (await RowsAsync(session, "MATCH (n) RETURN n")).Count.ShouldBe(before + 1);
        var node = (GraphNode)created.ShouldHaveSingleItem()[0]!;
        node.Labels.Order(StringComparer.Ordinal).ShouldBe(labels.Order(StringComparer.Ordinal));
        (await ColumnAsync(session, "MATCH (n:" + Chain(labels, "&") + ") RETURN n.name")).ShouldBe(["all", "new"]);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Label chains: a 10,000-name INSERT disjunction is COHDBG001, writes nothing and keeps the message short")]
    public async Task Execute_TenThousandNameInsertDisjunction_ShouldFailWithoutWritingAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create(new());
        await using var session = await SeedAsync(engine);
        int before = (await RowsAsync(session, "MATCH (n) RETURN n")).Count;

        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () =>
            await session.ExecuteAsync("INSERT (:" + Chain(labels, "|") + ")", cancellationToken: CancellationToken.None));

        // Assert
        error.Message.ShouldStartWith("COHDBG001", Case.Sensitive);
        error.Message.Length.ShouldBeLessThan(1_000);
        (await RowsAsync(session, "MATCH (n) RETURN n")).Count.ShouldBe(before);
    }

    /// <summary>
    /// Flattening a chain never crosses a precedence level: <c>!</c> &gt; <c>&amp;</c> &gt;
    /// <c>|</c>, in a pattern and in a labeled predicate alike.
    /// </summary>
    /// <param name="expression">A label expression mixing operators.</param>
    /// <param name="expected">The comma-separated names it selects, in identity order.</param>
    [Theory(DisplayName = "Cohesion Test [Graph] - Label chains: mixed precedence selects the same rows after flattening")]
    [InlineData("A|B&C", "a,ab,bc")]
    [InlineData("!A&B", "b,bc")]
    [InlineData("(A|B)&C", "bc")]
    [InlineData("A|B&!C", "a,b,ab")]
    [InlineData("!A&B|C", "b,c,bc")]
    [InlineData("A&B|B&C", "ab,bc")]
    [InlineData("A|(B|C)", "a,b,c,ab,bc")]
    [InlineData("(A|B)|C", "a,b,c,ab,bc")]
    [InlineData("!(A|B)", "c,u")]
    [InlineData("%&!(A&B)", "a,b,c,bc")]
    [InlineData("A&(B&C)", "")]
    public async Task Execute_MixedPrecedence_ShouldSelectExactRowsAsync(string expression, string expected)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create(new());
        var database = (IGraphDatabase)await engine.CreateDatabaseAsync("precedence", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("INSERT (:A {name: 'a'}), (:B {name: 'b'}), (:C {name: 'c'}), (:A:B {name: 'ab'}), " +
            "(:B:C {name: 'bc'}), ({name: 'u'})", cancellationToken: CancellationToken.None);

        // Act
        var pattern = await ColumnAsync(session, "MATCH (n:" + expression + ") RETURN n.name");
        var predicate = await ColumnAsync(session, "MATCH (n) WHERE n IS LABELED " + expression + " RETURN n.name");

        // Assert
        pattern.ShouldBe(expected.Length == 0 ? [] : expected.Split(','));
        predicate.ShouldBe(pattern);
    }

    /// <summary>
    /// The former 128-level label and predicate limits are gone: 300 nested groups, as deep as any
    /// default thread runs comfortably, plan and execute in a pattern, a labeled predicate and a
    /// predicate.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Graph] - Label chains: 300 nested groups, past the former 128 limit, execute")]
    public async Task Execute_NestingPastFormerLimit_ShouldRunAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create(new());
        await using var session = await SeedAsync(engine);
        const int depth = 300;
        string negations = string.Concat(Enumerable.Repeat("!(", depth)) + "X" + new string(')', depth);
        string groups = new string('(', depth) + "n.name = 'x'" + new string(')', depth);

        // Act
        var pattern = await ColumnAsync(session, "MATCH (n:" + negations + ") RETURN n.name");
        var labeled = await ColumnAsync(session, "MATCH (n) WHERE n:" + negations + " RETURN n.name");
        var predicate = await ColumnAsync(session, "MATCH (n) WHERE n.name <> 'u' AND " + groups + " RETURN n.name");

        // Assert: an even number of negations is X itself.
        pattern.ShouldBe(["x"]);
        labeled.ShouldBe(["x"]);
        predicate.ShouldBe(["x"]);
    }

    /// <summary>
    /// A statement nested deeper than the parsing thread's stack fails with <c>COHDBG007</c>, not a
    /// crash and not a parse error (the text is within the language), writes nothing, and leaves
    /// the session, and an explicit transaction it ran in, usable.
    /// </summary>
    /// <param name="template">The statement, with the nested text at <c>{0}</c>.</param>
    /// <param name="open">The text that opens one level.</param>
    /// <param name="leaf">The innermost text.</param>
    [Theory(DisplayName = "Cohesion Test [Graph] - Label chains: nesting past a small thread's stack is COHDBG007 and the session survives")]
    [InlineData("MATCH (n:{0}) RETURN n.name", "(", "X")]
    [InlineData("MATCH (n:{0}) RETURN n.name", "!(", "X")]
    [InlineData("MATCH (n) WHERE n IS LABELED {0} RETURN n.name", "(L0|", "X")]
    [InlineData("MATCH (n) WHERE {0} RETURN n.name", "(n.name = 'x' AND ", "n.name = 'x'")]
    [InlineData("INSERT (n:{0} {{name: 'deep'}})", "(", "X")]
    public async Task Execute_NestingPastTheStack_ShouldFailWithCohdbg007Async(string template, string open, string leaf)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create(new());
        await using var session = await SeedAsync(engine);
        const int depth = 100_000;
        string gql = string.Format(CultureInfo.InvariantCulture, template,
            string.Concat(Enumerable.Repeat(open, depth)) + leaf + new string(')', depth));
        await using var transaction = await session.BeginTransactionAsync(CancellationToken.None);
        await session.ExecuteAsync("INSERT (:Kept {name: 'kept'})", cancellationToken: CancellationToken.None);

        // Act
        var error = OnSmallThread(() => session.ExecuteAsync(gql, cancellationToken: CancellationToken.None).AsTask());

        // Assert
        var failure = error.ShouldBeOfType<DatabaseException>();
        failure.Message.ShouldStartWith("COHDBG007: Statement too complex", Case.Sensitive);
        failure.InnerException.ShouldBeOfType<InsufficientExecutionStackException>().Message.ShouldContain("GQL0009", Case.Sensitive);
        await transaction.CommitAsync(CancellationToken.None);
        (await ColumnAsync(session, "MATCH (n:Kept) RETURN n.name")).ShouldBe(["kept"]);
        (await ColumnAsync(session, "MATCH (n {name: 'deep'}) RETURN n.name")).ShouldBeEmpty();
        (await ColumnAsync(session, "MATCH (n:X) RETURN n.name")).ShouldBe(["x"]);
    }

    /// <summary>
    /// A typed request carrying the parser's GQL0009 reports the same COHDBG007 as the text seam,
    /// rather than executing a recovery tree.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Graph] - Label chains: a typed request whose parse ran out of stack is COHDBG007")]
    public async Task Execute_TypedRequestOutOfStack_ShouldFailWithCohdbg007Async()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create(new());
        await using var session = await SeedAsync(engine);
        string gql = "MATCH (n:" + new string('(', 100_000) + "X" + new string(')', 100_000) + ") RETURN n.name";
        GqlQueryStatement? statement = null;
        OnSmallThread(() =>
        {
            statement = (GqlQueryStatement)new GqlQueryParser().Parse(gql);
            return Task.CompletedTask;
        }).ShouldBeNull();

        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () =>
            await session.ExecuteAsync(new GraphQueryRequest(statement!), CancellationToken.None));

        // Assert
        statement!.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("GQL0009");
        error.Message.ShouldStartWith("COHDBG007", Case.Sensitive);
        (await ColumnAsync(session, "MATCH (n:X) RETURN n.name")).ShouldBe(["x"]);
    }

    /// <summary>
    /// A hand-built tree deeper than any thread's stack validates without recursion and fails in
    /// evaluation with COHDBG007, which aborts only the statement.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Graph] - Label chains: a hand-built tree deeper than the stack is COHDBG007 and the session survives")]
    public async Task Execute_HandBuiltTreePastTheStack_ShouldFailWithCohdbg007Async()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create(new());
        await using var session = await SeedAsync(engine);
        const int depth = 200_000;
        GqlLabelExpression negations = new GqlLabelName("X");
        GqlExpression predicate = new GqlBinaryExpression(new GqlPropertyExpression("n", "name"), "=", new GqlLiteralExpression("x"));
        for (int i = 0; i < depth; i++)
        {
            negations = new GqlLabelNegation(negations);
            predicate = new GqlLogicalExpression(GqlLogicalOperator.And, [predicate, new GqlLiteralExpression(true)]);
        }
        var empty = new Dictionary<string, object?>();
        GqlQueryStatement[] statements =
        [
            new(new GqlQueryExpression([new GqlPathPattern([new GqlNodePattern("n", [], empty) { LabelExpression = negations }], [])],
                null, [], [], false, [new GqlProjection("n", "name")])),
            new(new GqlQueryExpression([new GqlPathPattern([new GqlNodePattern("n", [], empty)], [])],
                new GqlLabeledPredicate("n", negations), [], [], false, [new GqlProjection("n", "name")])),
            new(new GqlQueryExpression([new GqlPathPattern([new GqlNodePattern("n", [], empty)], [])],
                predicate, [], [], false, [new GqlProjection("n", "name")])),
        ];

        foreach (var statement in statements)
        {
            // Act
            var error = await Should.ThrowAsync<DatabaseException>(async () =>
                await session.ExecuteAsync(new GraphQueryRequest(statement), CancellationToken.None));

            // Assert
            error.Message.ShouldStartWith("COHDBG007: Statement too complex", Case.Sensitive);
            error.InnerException.ShouldBeOfType<InsufficientExecutionStackException>();
            (await ColumnAsync(session, "MATCH (n:X) RETURN n.name")).ShouldBe(["x"]);
        }
    }

    // all: L0..L199; even: the even ones; x: X; u: unlabeled. all has one T<i> edge to even per type.
    private static async Task<IDatabaseSession> SeedAsync(GraphDatabaseEngine engine)
    {
        var database = (IGraphDatabase)await engine.CreateDatabaseAsync("chains", CancellationToken.None);
        var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("INSERT (:" + string.Join("&", labels) + " {name: 'all'}), " +
            "(:" + string.Join("&", labels.Where((_, i) => i % 2 == 0)) + " {name: 'even'}), (:X {name: 'x'}), ({name: 'u'})",
            cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("MATCH (a), (b) WHERE a.name = 'all' AND b.name = 'even' INSERT " +
            string.Join(", ", types.Select(type => "(a)-[:" + type + " {name: '" + type.ToLowerInvariant() + "'}]->(b)")),
            cancellationToken: CancellationToken.None);
        return session;
    }

    private static string Chain(string[] names, string separator)
        => string.Join(separator, Enumerable.Range(0, chainLength).Select(i => names[i % names.Length]));

    private static async Task<List<object?[]>> RowsAsync(IDatabaseSession session, string gql)
    {
        await using var result = (QueryResultSet)await session.ExecuteAsync(gql, cancellationToken: CancellationToken.None);
        var rows = new List<object?[]>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        {
            rows.Add(Enumerable.Range(0, row.FieldCount).Select(row.GetValue).ToArray());
        }
        return rows;
    }

    private static async Task<List<string?>> ColumnAsync(IDatabaseSession session, string gql)
        => (await RowsAsync(session, gql)).Select(row => (string?)row[0]).ToList();

    /// <summary>
    /// Runs <paramref name="action"/> to completion on a thread with a 512 KB stack and returns
    /// what it threw. The text seam parses before its first await, so the parse runs on this thread.
    /// </summary>
    private static Exception? OnSmallThread(Func<Task> action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { action().GetAwaiter().GetResult(); }
            catch (Exception exception) { error = exception; }
        }, 512 * 1024);
        thread.Start();
        thread.Join();
        return error;
    }
}
