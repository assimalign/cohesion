using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Graph.Internal;
using Assimalign.Cohesion.Database.Graph.Language;
using Assimalign.Cohesion.Database.Graph.Storage;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Graph.Tests;

/// <summary>
/// Label expressions and predicates have no fixed length or nesting limit (#1139 follow-up, owner
/// decision 2026-10-02: "do what Neo4j does"). 10,000-name chains of every operator plan and run in
/// node patterns, edge patterns, labeled predicates and insertions; flattening keeps
/// <c>!</c> &gt; <c>&amp;</c> &gt; <c>|</c>; a statement nested deeper than the executing thread's
/// stack fails with <c>COHDBG008</c> while the session keeps serving; an element whose labels and
/// properties outgrow its storage record fails with <c>COHDBG009</c> the same way; and index-anchor
/// selection stays linear however many labels, equalities and indexes meet.
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
        var database = await engine.CreateDatabaseAsync("distinct", CancellationToken.None);
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
        var database = await engine.CreateDatabaseAsync("precedence", CancellationToken.None);
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
    /// A statement nested deeper than the parsing thread's stack fails with <c>COHDBG008</c>, not a
    /// crash and not a parse error (the text is within the language), writes nothing, and leaves
    /// the session usable. Like any failed statement (#1188), it aborts the explicit transaction it
    /// ran in: the commit fails with <c>COHDBG007</c> and the transaction's earlier write is gone.
    /// </summary>
    /// <param name="template">The statement, with the nested text at <c>{0}</c>.</param>
    /// <param name="open">The text that opens one level.</param>
    /// <param name="leaf">The innermost text.</param>
    [Theory(DisplayName = "Cohesion Test [Graph] - Label chains: nesting past a small thread's stack is COHDBG008 and the session survives")]
    [InlineData("MATCH (n:{0}) RETURN n.name", "(", "X")]
    [InlineData("MATCH (n:{0}) RETURN n.name", "!(", "X")]
    [InlineData("MATCH (n) WHERE n IS LABELED {0} RETURN n.name", "(L0|", "X")]
    [InlineData("MATCH (n) WHERE {0} RETURN n.name", "(n.name = 'x' AND ", "n.name = 'x'")]
    [InlineData("INSERT (n:{0} {{name: 'deep'}})", "(", "X")]
    public async Task Execute_NestingPastTheStack_ShouldFailWithCohdbg008Async(string template, string open, string leaf)
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
        failure.Message.ShouldStartWith("COHDBG008: Statement too complex", Case.Sensitive);
        failure.InnerException.ShouldBeOfType<InsufficientExecutionStackException>().Message.ShouldContain("GQL0009", Case.Sensitive);
        transaction.State.ShouldBe(TransactionState.Faulted);
        (await Should.ThrowAsync<DatabaseException>(async () => await transaction.CommitAsync(CancellationToken.None)))
            .Message.ShouldStartWith("COHDBG007: ", Case.Sensitive);
        // The abort also rolled back the definition of Kept, so match the node by its property.
        (await ColumnAsync(session, "MATCH (n {name: 'kept'}) RETURN n.name")).ShouldBeEmpty();
        (await ColumnAsync(session, "MATCH (n {name: 'deep'}) RETURN n.name")).ShouldBeEmpty();
        (await ColumnAsync(session, "MATCH (n:X) RETURN n.name")).ShouldBe(["x"]);
    }

    /// <summary>
    /// A typed request carrying the parser's GQL0009 reports the same COHDBG008 as the text seam,
    /// rather than executing a recovery tree.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Graph] - Label chains: a typed request whose parse ran out of stack is COHDBG008")]
    public async Task Execute_TypedRequestOutOfStack_ShouldFailWithCohdbg008Async()
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
        error.Message.ShouldStartWith("COHDBG008", Case.Sensitive);
        (await ColumnAsync(session, "MATCH (n:X) RETURN n.name")).ShouldBe(["x"]);
    }

    /// <summary>
    /// A hand-built tree deeper than any thread's stack validates without recursion and fails in
    /// evaluation with COHDBG008, which aborts only the statement.
    /// </summary>
    /// <remarks>
    /// The statement runs on whatever thread the session continues on, so the depth has to defeat
    /// every platform's default stack, not only Windows' 1 to 1.5 MB: Linux threads default to
    /// 8 MB (RLIMIT_STACK). A level of the smallest recursion (<c>return !Matches(...)</c>) can cost
    /// as little as 32 bytes, so 1,000,000 levels need at least 32 MB. At 200,000 levels the
    /// negation chain fitted in a Linux thread pool thread and evaluated without failing.
    /// </remarks>
    [Fact(DisplayName = "Cohesion Test [Graph] - Label chains: a hand-built tree deeper than the stack is COHDBG008 and the session survives")]
    public async Task Execute_HandBuiltTreePastTheStack_ShouldFailWithCohdbg008Async()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create(new());
        await using var session = await SeedAsync(engine);
        const int depth = 1_000_000;
        var always = new GqlLiteralExpression(true);
        GqlLabelExpression negations = new GqlLabelName("X");
        GqlExpression predicate = new GqlBinaryExpression(new GqlPropertyExpression("n", "name"), "=", new GqlLiteralExpression("x"));
        for (int i = 0; i < depth; i++)
        {
            negations = new GqlLabelNegation(negations);
            predicate = new GqlLogicalExpression(GqlLogicalOperator.And, [predicate, always]);
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
            error.Message.ShouldStartWith("COHDBG008: Statement too complex", Case.Sensitive);
            error.InnerException.ShouldBeOfType<InsufficientExecutionStackException>();
            (await ColumnAsync(session, "MATCH (n:X) RETURN n.name")).ShouldBe(["x"]);
        }
    }

    /// <summary>
    /// With no label cap, a long conjunction can outgrow the one graph record a node's labels and
    /// properties share, as a long property always could. The store refuses the record before it
    /// writes it, and the engine reports <c>COHDBG009</c>: the statement fails like any other,
    /// aborting the explicit transaction it ran in (#1188; catalog labels it defined are rolled back
    /// and the commit fails with <c>COHDBG007</c>), and the session serves the next transaction.
    /// </summary>
    /// <param name="gql">An insertion whose record exceeds 8,092 bytes.</param>
    [Theory(DisplayName = "Cohesion Test [Graph] - Element size: a record past 8,092 bytes is COHDBG009 and the session survives")]
    [MemberData(nameof(OversizedInsertions))]
    public async Task Execute_OversizedRecord_ShouldFailWithCohdbg009Async(string gql)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create(new());
        await using var session = await SeedAsync(engine);
        int labelCount = (await RowsAsync(session, "SHOW LABELS")).Count;
        var transaction = await session.BeginTransactionAsync(CancellationToken.None);
        await session.ExecuteAsync("INSERT (:Kept {name: 'kept'})", cancellationToken: CancellationToken.None);

        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () =>
            await session.ExecuteAsync(gql, cancellationToken: CancellationToken.None));

        // Assert
        error.Message.ShouldStartWith("COHDBG009: ", Case.Sensitive);
        error.Message.ShouldContain("8092 bytes one graph record can hold", Case.Sensitive);
        error.InnerException.ShouldBeOfType<GraphElementTooLargeException>();
        transaction.State.ShouldBe(TransactionState.Faulted);
        (await Should.ThrowAsync<DatabaseException>(async () => await transaction.CommitAsync(CancellationToken.None)))
            .Message.ShouldStartWith("COHDBG007: ", Case.Sensitive);
        await transaction.DisposeAsync();
        (await RowsAsync(session, "SHOW LABELS")).Count.ShouldBe(labelCount);
        await using (var next = await session.BeginTransactionAsync(CancellationToken.None))
        {
            await session.ExecuteAsync("INSERT (:Kept {name: 'next'})", cancellationToken: CancellationToken.None);
            await next.CommitAsync(CancellationToken.None);
        }
        (await ColumnAsync(session, "MATCH (n:Kept) RETURN n.name")).ShouldBe(["next"]);
        (await ColumnAsync(session, "MATCH (n:X) RETURN n.name")).ShouldBe(["x"]);
    }

    /// <summary>
    /// Insertions past the record limit: 300 labels of 33 characters, a 9,000-character node
    /// property, and a 9,000-character relationship property.
    /// </summary>
    public static TheoryData<string> OversizedInsertions => new()
    {
        "INSERT (:" + string.Join("&", Enumerable.Range(0, 300).Select(i => "Label_with_a_fairly_long_name_" + i.ToString("D3", CultureInfo.InvariantCulture))) + ")",
        "INSERT (:Big {s: '" + new string('s', 9_000) + "'})",
        "MATCH (a:X) INSERT (a)-[:BIG {s: '" + new string('s', 9_000) + "'}]->(a)",
    };

    /// <summary>
    /// An indexed value past the 1,016-byte index key fails its insertion, and an index build over
    /// one, with <c>COHDBG009</c>; a search for one matches nothing, since no write can store it.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Graph] - Element size: an indexed value past the index key is COHDBG009 to write and matches nothing")]
    public async Task Execute_OversizedIndexedValue_ShouldFailToWriteAndMatchNothingAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create(new());
        var database = await engine.CreateDatabaseAsync("keys", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        string longName = new('n', 600);
        await session.ExecuteAsync("INSERT (:X {name: 'x'}), (:Y {name: '" + longName + "'})", cancellationToken: CancellationToken.None);
        var schema = GraphSchema.Open(database, session);
        await schema.CreateIndexAsync("X", "by_name", "name");

        // Act
        var insert = await Should.ThrowAsync<DatabaseException>(async () =>
            await session.ExecuteAsync("INSERT (:X {name: '" + longName + "'})", cancellationToken: CancellationToken.None));
        var build = await Should.ThrowAsync<DatabaseException>(async () => await schema.CreateIndexAsync("Y", "by_name", "name"));
        var inline = await ColumnAsync(session, "MATCH (n:X {name: '" + longName + "'}) RETURN n.name");
        var equality = await ColumnAsync(session, "MATCH (n:X) WHERE n.name = '" + longName + "' RETURN n.name");

        // Assert
        insert.Message.ShouldStartWith("COHDBG009: An indexed property value encodes to ", Case.Sensitive);
        insert.Message.ShouldContain("1016-byte index key", Case.Sensitive);
        build.Message.ShouldStartWith("COHDBG009: ", Case.Sensitive);
        inline.ShouldBeEmpty();
        equality.ShouldBeEmpty();
        (await ColumnAsync(session, "MATCH (n:X {name: 'x'}) RETURN n.name")).ShouldBe(["x"]);
        (await RowsAsync(session, "SHOW INDEXES")).ShouldHaveSingleItem()[2].ShouldBe("X");
    }

    /// <summary>
    /// Anchor selection reads the visible indexes once and each variable's equalities once, so 20
    /// indexes against a 100-label conjunction and 10,000 equalities plan in linear time, where a
    /// lookup per label, equality and index took minutes. The choice is unchanged: the first label
    /// with an index on a key the node has a value for, then that label's key whose value comes
    /// first, inline properties before WHERE equalities.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Graph] - Planner: anchor selection is linear in labels, equalities and indexes and chooses as before")]
    public async Task Plan_ManyLabelsEqualitiesAndIndexes_ShouldChooseAnchorInLinearTimeAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create(new());
        var database = await engine.CreateDatabaseAsync("anchor-cost", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        string[] hundred = labels.Take(100).ToArray();
        await session.ExecuteAsync("INSERT (:" + string.Join("&", hundred) + " {name: 'all'}), (:A:B {p: 1, q: 2, name: 'ab'}), " +
            "(:A {p: 1, q: 3, name: 'a'})", cancellationToken: CancellationToken.None);
        var schema = GraphSchema.Open(database, session);
        for (int i = 0; i < 20; i++) { await schema.CreateIndexAsync(hundred[i], "ix" + i.ToString(CultureInfo.InvariantCulture), "zz"); }
        await schema.CreateIndexAsync("A", "by_q", "q");
        await schema.CreateIndexAsync("A", "by_p", "p");
        await schema.CreateIndexAsync("B", "by_p", "p");
        string conjunction = string.Join("&", hundred);
        string sameKey = "MATCH (n:" + conjunction + ") WHERE " + string.Join(" AND ", Enumerable.Repeat("n.name = 'all'", chainLength)) + " RETURN n.name";
        string distinctKeys = "MATCH (n:" + conjunction + ") WHERE " +
            string.Join(" AND ", Enumerable.Range(0, chainLength).Select(i => "n.k" + i.ToString(CultureInfo.InvariantCulture) + " = 1")) + " RETURN n.name";

        // Act
        var timer = Stopwatch.StartNew();
        var same = await ColumnAsync(session, sameKey);
        var distinct = await ColumnAsync(session, distinctKeys);
        timer.Stop();
        var labelOrder = (await PlanAsync(database, session, "MATCH (n:A&B {p: 1}) WHERE n.q = 2 RETURN n.name")).Matches.Single().Anchor;
        var reversed = (await PlanAsync(database, session, "MATCH (n:B&A {q: 2}) WHERE n.p = 1 RETURN n.name")).Matches.Single().Anchor;
        var inlineFirst = (await PlanAsync(database, session, "MATCH (n:A {q: 2, p: 1}) RETURN n.name")).Matches.Single().Anchor;
        var nullInline = (await PlanAsync(database, session, "MATCH (n:A {p: null}) WHERE n.q = 3 AND n.p = 1 RETURN n.name")).Matches.Single().Anchor;
        var unindexed = (await PlanAsync(database, session, "MATCH (n:" + conjunction + ") WHERE n.name = 'all' RETURN n.name")).Matches.Single().Anchor;

        // Assert
        timer.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(15));
        same.ShouldBe(["all"]);
        distinct.ShouldBeEmpty();
        (labelOrder.Label, labelOrder.Property, labelOrder.Value).ShouldBe(("A", "p", (object?)1L));
        (reversed.Label, reversed.Property, reversed.Value).ShouldBe(("B", "p", (object?)1L));
        (inlineFirst.Label, inlineFirst.Property, inlineFirst.Value).ShouldBe(("A", "q", (object?)2L));
        (nullInline.Label, nullInline.Property, nullInline.Value).ShouldBe(("A", "q", (object?)3L));
        (unindexed.Label, unindexed.Property).ShouldBe(("L0", null));
        (await ColumnAsync(session, "MATCH (n:A&B {p: 1}) WHERE n.q = 2 RETURN n.name")).ShouldBe(["ab"]);
        (await ColumnAsync(session, "MATCH (n:B&A {q: 2}) WHERE n.p = 1 RETURN n.name")).ShouldBe(["ab"]);
        (await ColumnAsync(session, "MATCH (n:A {p: null}) WHERE n.q = 3 AND n.p = 1 RETURN n.name")).ShouldBeEmpty();
        (await ColumnAsync(session, "MATCH (n:A) WHERE n.q = 3 AND n.p = 1 RETURN n.name")).ShouldBe(["a"]);
    }

    /// <summary>
    /// A diagnostic quotes at most 256 UTF-16 code units of an expression. Where the cut would fall
    /// between the halves of a surrogate pair it backs off one unit, so the message stays
    /// well-formed UTF-16 and reaches a wire client without a replacement character.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Graph] - Label chains: a cut diagnostic never splits a surrogate pair")]
    public async Task Describe_CutInsideSurrogatePair_ShouldKeepWellFormedTextAsync()
    {
        // Arrange: the quoted name puts a high surrogate at index 255.
        string emoji = string.Concat(Enumerable.Repeat("\U0001F600", 200));
        var expression = new GqlLabelDisjunction([new GqlLabelName(emoji), new GqlLabelName("X")]);
        await using var engine = GraphDatabaseEngine.Create(new());
        var database = await engine.CreateDatabaseAsync("surrogates", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var strict = new UTF8Encoding(false, true);

        // Act
        string described = GraphLabelEvaluator.Describe(expression);
        var error = await Should.ThrowAsync<DatabaseException>(async () =>
            await session.ExecuteAsync("INSERT (:\"" + emoji + "\"|X)", cancellationToken: CancellationToken.None));

        // Assert
        char.IsHighSurrogate(expression.ToString()[255]).ShouldBeTrue();
        described.ShouldBe(string.Concat(expression.ToString().AsSpan(0, 255), "..."));
        Should.NotThrow(() => strict.GetByteCount(described));
        error.Message.ShouldStartWith("COHDBG001", Case.Sensitive);
        error.Message.ShouldContain(described, Case.Sensitive);
        Should.NotThrow(() => strict.GetByteCount(error.Message));
    }

    // all: L0..L199; even: the even ones; x: X; u: unlabeled. all has one T<i> edge to even per type.
    private static async Task<GraphDatabaseSession> SeedAsync(GraphDatabaseEngine engine)
    {
        var database = await engine.CreateDatabaseAsync("chains", CancellationToken.None);
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

    private static async Task<List<object?[]>> RowsAsync(GraphDatabaseSession session, string gql)
    {
        await using var result = (QueryResultSet)await session.ExecuteAsync(gql, cancellationToken: CancellationToken.None);
        var rows = new List<object?[]>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        {
            rows.Add(Enumerable.Range(0, row.FieldCount).Select(row.GetValue).ToArray());
        }
        return rows;
    }

    private static async Task<List<string?>> ColumnAsync(GraphDatabaseSession session, string gql)
        => (await RowsAsync(session, gql)).Select(row => (string?)row[0]).ToList();

    private static ValueTask<GraphPlan> PlanAsync(GraphDatabase database, GraphDatabaseSession session, string gql)
    {
        return database.RunAsync(session, operation => new ValueTask<GraphPlan>(
            new GraphPlanner(database, operation.Context.Snapshot).Plan(GraphQueryRequest.FromGql(gql).Statement.GqlExpression)), CancellationToken.None);
    }

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
