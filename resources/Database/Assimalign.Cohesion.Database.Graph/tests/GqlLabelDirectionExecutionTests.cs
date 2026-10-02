using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Graph.Internal;
using Assimalign.Cohesion.Database.Graph.Language;

namespace Assimalign.Cohesion.Database.Graph.Tests;

/// <summary>
/// Executes ISO/IEC 39075 label expressions, labeled predicates and every directed ISO edge form
/// against a running engine (#1139), and proves the forms the engine cannot honor fail before
/// anything is read or written: tilde edges, Cypher arrows, and insertions that would have to
/// guess a label or a direction.
/// </summary>
public sealed class GqlLabelDirectionExecutionTests
{
    // Node identities follow insertion order: a, b, ab, c, then the unlabeled u.
    private const string labelSeed =
        "INSERT (a:A {name: 'a'}), (b:B {name: 'b'}), (ab:A:B {name: 'ab'}), (c:C {name: 'c'}), (u {name: 'u'}), " +
        "(a)-[:T {name: 't'}]->(b), (b)-[:U {name: 'u1'}]->(c)";

    /// <param name="gql">A read over the seeded graph.</param>
    /// <param name="expected">The comma-separated names it returns, in node-identity order.</param>
    [Theory(DisplayName = "Cohesion Test [Graph] - Label expressions: |, &, !, % and IS select exactly the expected nodes")]
    [InlineData("MATCH (n:A|B) RETURN n.name", "a,b,ab")]
    [InlineData("MATCH (n:A&B) RETURN n.name", "ab")]
    [InlineData("MATCH (n:A:B) RETURN n.name", "ab")]
    [InlineData("MATCH (n:!A) RETURN n.name", "b,c,u")]
    [InlineData("MATCH (n:%) RETURN n.name", "a,b,ab,c")]
    [InlineData("MATCH (n:!%) RETURN n.name", "u")]
    [InlineData("MATCH (n:(A|B)&!C) RETURN n.name", "a,b,ab")]
    [InlineData("MATCH (n:A&!B) RETURN n.name", "a")]
    [InlineData("MATCH (n:!A&!B) RETURN n.name", "c,u")]
    [InlineData("MATCH (n:A|B&!A) RETURN n.name", "a,b,ab")]
    [InlineData("MATCH (n IS A|C) RETURN n.name", "a,ab,c")]
    [InlineData("MATCH (n IS %&!(A|B)) RETURN n.name", "c")]
    [InlineData("MATCH (n:!(!A)) RETURN n.name", "a,ab")]
    [InlineData("MATCH (n:C|!%) RETURN n.name", "c,u")]
    public async Task Execute_NodeLabelExpression_ShouldSelectExactRowsAsync(string gql, string expected)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create(new());
        await using var session = await SeedAsync(engine, labelSeed);

        // Act
        var names = await ColumnAsync(session, gql);

        // Assert
        names.ShouldBe(expected.Split(','));
    }

    /// <param name="gql">A relationship read over the seeded graph.</param>
    /// <param name="expected">The comma-separated relationship names it returns.</param>
    [Theory(DisplayName = "Cohesion Test [Graph] - Label expressions: T|U, !T and % select exactly the expected relationships")]
    [InlineData("MATCH ()-[r:T|U]->() RETURN r.name", "t,u1")]
    [InlineData("MATCH ()-[r:!T]->() RETURN r.name", "u1")]
    [InlineData("MATCH ()-[r:%]->() RETURN r.name", "t,u1")]
    [InlineData("MATCH ()-[r IS T]->() RETURN r.name", "t")]
    [InlineData("MATCH ()-[r:!%]->() RETURN r.name", "")]
    [InlineData("MATCH ()-[r:T&U]->() RETURN r.name", "")]
    [InlineData("MATCH (x:A)-[r:!(T|U)]-() RETURN r.name", "")]
    [InlineData("MATCH (x:B)-[r:T|U]-(y) RETURN r.name", "t,u1")]
    public async Task Execute_EdgeLabelExpression_ShouldSelectExactRowsAsync(string gql, string expected)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create(new());
        await using var session = await SeedAsync(engine, labelSeed);

        // Act
        var names = await ColumnAsync(session, gql);

        // Assert
        names.ShouldBe(expected.Length == 0 ? [] : expected.Split(','));
    }

    /// <summary>
    /// Storage holds only directed edges, so the ISO any-direction and left-or-right forms match an
    /// edge in either orientation: one A to B edge yields (A,B) and (B,A), and a self-loop one row.
    /// </summary>
    /// <param name="edge">The edge spelling between <c>(x)</c> and <c>(y)</c>.</param>
    [Theory(DisplayName = "Cohesion Test [Graph] - Edges: -[]-, -, <-[]-> and <-> match both orientations, a self-loop once")]
    [InlineData("-[r]-")]
    [InlineData("-")]
    [InlineData("<-[r]->")]
    [InlineData("<->")]
    public async Task Execute_EitherDirectionEdge_ShouldMatchBothOrientationsAsync(string edge)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create(new());
        await using var session = await SeedAsync(engine, "INSERT (:A {name: 'A'})-[:T]->(:B {name: 'B'})");
        var loopDatabase = (IGraphDatabase)await engine.CreateDatabaseAsync("loop", CancellationToken.None);
        await using var loop = await loopDatabase.CreateSessionAsync(CancellationToken.None);
        await loop.ExecuteAsync("INSERT (s:S {name: 'S'})-[:L]->(s)", cancellationToken: CancellationToken.None);

        // Act
        var pairs = await RowsAsync(session, $"MATCH (x){edge}(y) RETURN x.name, y.name");
        var loops = await RowsAsync(loop, $"MATCH (x){edge}(y) RETURN x.name, y.name");

        // Assert
        pairs.Select(row => $"{row[0]}>{row[1]}").ShouldBe(["A>B", "B>A"]);
        loops.Select(row => $"{row[0]}>{row[1]}").ShouldBe(["S>S"]);
    }

    /// <param name="edge">A directed edge spelling between <c>(x)</c> and <c>(y)</c>.</param>
    /// <param name="expected">The single row it returns, as source&gt;target names.</param>
    [Theory(DisplayName = "Cohesion Test [Graph] - Edges: -> and <- keep their direction against the stored edge")]
    [InlineData("->", "A>B")]
    [InlineData("<-", "B>A")]
    [InlineData("-[r]->", "A>B")]
    [InlineData("<-[r]-", "B>A")]
    public async Task Execute_DirectedAbbreviatedEdge_ShouldKeepItsDirectionAsync(string edge, string expected)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create(new());
        await using var session = await SeedAsync(engine, "INSERT (:A {name: 'A'})-[:T]->(:B {name: 'B'})");

        // Act
        var rows = await RowsAsync(session, $"MATCH (x){edge}(y) RETURN x.name, y.name");

        // Assert
        rows.Select(row => $"{row[0]}>{row[1]}").ShouldBe([expected]);
    }

    /// <summary>
    /// A disjunction or negation must never feed the index anchor, or rows of the other labels
    /// would be dropped; a conjunction still anchors on a member's index.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Graph] - Planner: | and ! never anchor on an index, & anchors on a member's index")]
    public async Task Plan_LabelExpressionWithIndex_ShouldAnchorOnlyConjunctionsAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create(new());
        var database = (IGraphDatabase)await engine.CreateDatabaseAsync("anchors", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync(
            "INSERT (:A {k: 1, name: 'a1'}), (:B {k: 1, name: 'b1'}), (:A:B {k: 1, name: 'ab1'}), (:C {k: 1, name: 'c1'}), " +
            "({k: 1, name: 'u1'}), (:A {k: 2, name: 'a2'})", cancellationToken: CancellationToken.None);
        await GraphSchema.Open(database, session).CreateIndexAsync("A", "by_k", "k");

        // Act
        var disjunction = (await PlanAsync(database, session, "MATCH (n:A|B {k: 1}) RETURN n.name")).Matches.Single().Anchor;
        var negation = (await PlanAsync(database, session, "MATCH (n:!A {k: 1}) RETURN n.name")).Matches.Single().Anchor;
        var conjunction = (await PlanAsync(database, session, "MATCH (n:A&B {k: 1}) RETURN n.name")).Matches.Single().Anchor;
        var predicate = (await PlanAsync(database, session, "MATCH (n) WHERE n:A AND n.k = 1 RETURN n.name")).Matches.Single().Anchor;

        // Assert
        disjunction.Property.ShouldBeNull();
        disjunction.Label.ShouldBeNull();
        (await ColumnAsync(session, "MATCH (n:A|B {k: 1}) RETURN n.name")).ShouldBe(["a1", "b1", "ab1"]);
        negation.Property.ShouldBeNull();
        negation.Label.ShouldBeNull();
        (await ColumnAsync(session, "MATCH (n:!A {k: 1}) RETURN n.name")).ShouldBe(["b1", "c1", "u1"]);
        conjunction.Label.ShouldBe("A");
        conjunction.Property.ShouldBe("k");
        conjunction.Value.ShouldBe(1L);
        (await ColumnAsync(session, "MATCH (n:A&B {k: 1}) RETURN n.name")).ShouldBe(["ab1"]);
        predicate.Property.ShouldBeNull();
        (await ColumnAsync(session, "MATCH (n) WHERE n:A AND n.k = 1 RETURN n.name")).ShouldBe(["a1", "ab1"]);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Insertion: :A&B and :A:B create one node carrying both labels")]
    public async Task Execute_InsertLabelConjunction_ShouldCreateOneNodeWithBothLabelsAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create(new());
        await using var session = await SeedAsync(engine, "INSERT (:A), (:B)");

        // Act
        var ampersand = await session.ExecuteAsync("INSERT (:A&B {name: 'amp'})", cancellationToken: CancellationToken.None);
        var colons = await session.ExecuteAsync("INSERT (:A:B {name: 'colons'})", cancellationToken: CancellationToken.None);
        var nodes = await RowsAsync(session, "MATCH (n:A&B) RETURN n");

        // Assert
        ampersand.AffectedCount.ShouldBe(1);
        colons.AffectedCount.ShouldBe(1);
        nodes.Count.ShouldBe(2);
        nodes.ShouldAllBe(row => ((GraphNode)row[0]!).Labels.SequenceEqual(new[] { "A", "B" }));
        (await RowsAsync(session, "MATCH (n) RETURN n")).Count.ShouldBe(4);
    }

    /// <param name="gql">An insertion that would have to guess a label, a type or a direction.</param>
    [Theory(DisplayName = "Cohesion Test [Graph] - Insertion: |, !, %, either-direction and untyped edges are COHDBG001 and write nothing")]
    [InlineData("INSERT (:A|B)")]
    [InlineData("INSERT (:!A)")]
    [InlineData("INSERT (:%)")]
    [InlineData("INSERT (:A&!B)")]
    [InlineData("INSERT (a:A)<-[:T]->(b:B)")]
    [InlineData("INSERT (a:A)<->(b:B)")]
    [InlineData("INSERT (a:A)->(b:B)")]
    [InlineData("INSERT (a:A)-(b:B)")]
    [InlineData("INSERT (a:A)<-(b:B)")]
    [InlineData("INSERT (a:A)-[:T|U]->(b:B)")]
    [InlineData("INSERT (a:A)-[:%]->(b:B)")]
    [InlineData("MATCH (a:A), (b:B) INSERT (a)<-[:T]->(b)")]
    [InlineData("MATCH (a:A) INSERT (a)-[:T]->(:!A)")]
    public async Task Execute_GuessingInsertion_ShouldFailWithoutWritingAsync(string gql)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create(new());
        await using var session = await SeedAsync(engine, "INSERT (:A {name: 'a'}), (:B {name: 'b'})");

        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () =>
            await session.ExecuteAsync(gql, cancellationToken: CancellationToken.None));

        // Assert
        error.Message.ShouldStartWith("COHDBG001", Case.Sensitive);
        (await RowsAsync(session, "MATCH (n) RETURN n")).Count.ShouldBe(2);
        (await RowsAsync(session, "MATCH ()-[r]->() RETURN r")).ShouldBeEmpty();
    }

    /// <summary>
    /// A delimited name may be all whitespace, but storage cannot hold such a label, type or
    /// property key. Insertion rejects it with a coded error before anything is written, instead
    /// of letting the store's argument check escape as an uncoded exception.
    /// </summary>
    /// <param name="gql">An insertion naming an all-whitespace label, type or property key.</param>
    [Theory(DisplayName = "Cohesion Test [Graph] - Insertion: an all-whitespace label, type or property key is COHDBG001 and writes nothing")]
    [InlineData("INSERT (:\" \")")]
    [InlineData("INSERT (n IS \" \")")]
    [InlineData("INSERT (:A&\" \")")]
    [InlineData("INSERT (:A:\"\t\")")]
    [InlineData("INSERT (:A {\" \": 1})")]
    [InlineData("INSERT (a:A)-[:\" \"]->(b:B)")]
    [InlineData("INSERT (a:A)<-[r IS \" \"]-(b:B)")]
    [InlineData("INSERT (a:A)-[:T {\" \": 1}]->(b:B)")]
    [InlineData("MATCH (a:A) INSERT (a)-[:\" \"]->(:B)")]
    public async Task Execute_WhitespaceInsertName_ShouldFailWithoutWritingAsync(string gql)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create(new());
        await using var session = await SeedAsync(engine, "INSERT (:A {name: 'a'}), (:B {name: 'b'})");

        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () =>
            await session.ExecuteAsync(gql, cancellationToken: CancellationToken.None));

        // Assert
        error.Message.ShouldStartWith("COHDBG001", Case.Sensitive);
        error.Message.ShouldContain("only whitespace", Case.Sensitive);
        (await RowsAsync(session, "MATCH (n) RETURN n")).Count.ShouldBe(2);
        (await RowsAsync(session, "MATCH ()-[r]->() RETURN r")).ShouldBeEmpty();
        (await RowsAsync(session, "SHOW LABELS")).Count.ShouldBe(2);
    }

    /// <param name="gql">A read naming a label or type the catalog does not define.</param>
    [Theory(DisplayName = "Cohesion Test [Graph] - Label expressions: an unknown name anywhere in MATCH is COHDBG002")]
    [InlineData("MATCH (n:A|Missing) RETURN n")]
    [InlineData("MATCH (n:!Missing) RETURN n")]
    [InlineData("MATCH (n:A&!(B|Missing)) RETURN n")]
    [InlineData("MATCH (n IS Missing|%) RETURN n")]
    [InlineData("MATCH ()-[r:T|Missing]->() RETURN r")]
    [InlineData("MATCH ()-[r:!Missing]->() RETURN r")]
    [InlineData("MATCH (n) WHERE n:Missing RETURN n")]
    [InlineData("MATCH (n) WHERE n IS NOT LABELED A|Missing RETURN n")]
    [InlineData("MATCH ()-[r]->() WHERE r:Missing RETURN r")]
    [InlineData("MATCH ()-[r]->() WHERE r:A RETURN r")]
    [InlineData("MATCH (n) WHERE n:T RETURN n")]
    public async Task Execute_UnknownLabelOrType_ShouldReportCohdbg002Async(string gql)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create(new());
        await using var session = await SeedAsync(engine, labelSeed);

        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () =>
            await session.ExecuteAsync(gql, cancellationToken: CancellationToken.None));

        // Assert
        error.Message.ShouldStartWith("COHDBG002", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Labeled predicate: IS LABELED, n: and the pattern form agree; IS NOT LABELED and r:T filter")]
    public async Task Execute_LabeledPredicate_ShouldFilterLikeThePatternAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create(new());
        await using var session = await SeedAsync(engine, labelSeed);

        // Act
        var pattern = await ColumnAsync(session, "MATCH (n:A|B) RETURN n.name");
        var labeled = await ColumnAsync(session, "MATCH (n) WHERE n IS LABELED A|B RETURN n.name");
        var colon = await ColumnAsync(session, "MATCH (n) WHERE n:A|B RETURN n.name");
        var notA = await ColumnAsync(session, "MATCH (n) WHERE n IS NOT LABELED A RETURN n.name");
        var typed = await ColumnAsync(session, "MATCH ()-[r]->() WHERE r:T RETURN r.name");
        var notTyped = await ColumnAsync(session, "MATCH ()-[r]->() WHERE r IS NOT LABELED T RETURN r.name");
        var combined = await ColumnAsync(session, "MATCH (x)-[r]->(y) WHERE x:A AND y IS LABELED B AND r:% RETURN r.name");

        // Assert
        pattern.ShouldBe(["a", "b", "ab"]);
        labeled.ShouldBe(pattern);
        colon.ShouldBe(pattern);
        notA.ShouldBe(["b", "c", "u"]);
        typed.ShouldBe(["t"]);
        notTyped.ShouldBe(["u1"]);
        combined.ShouldBe(["t"]);
    }

    /// <param name="gql">A labeled predicate over an unusable variable.</param>
    /// <param name="code">The binding code it reports.</param>
    [Theory(DisplayName = "Cohesion Test [Graph] - Labeled predicate: a path operand is COHDBG003, an unbound variable COHDBG001")]
    [InlineData("MATCH p = (a)-[r]->(b) WHERE p:A RETURN a", "COHDBG003")]
    [InlineData("MATCH p = (a)-[r]->(b) WHERE p IS LABELED A RETURN a", "COHDBG003")]
    [InlineData("MATCH (n) WHERE m:A RETURN n", "COHDBG001")]
    [InlineData("MATCH (n) WHERE m IS NOT LABELED A RETURN n", "COHDBG001")]
    public async Task Execute_LabeledPredicateOverUnusableVariable_ShouldFailAsync(string gql, string code)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create(new());
        await using var session = await SeedAsync(engine, labelSeed);

        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () =>
            await session.ExecuteAsync(gql, cancellationToken: CancellationToken.None));

        // Assert
        error.Message.ShouldStartWith(code, Case.Sensitive);
    }

    /// <summary>
    /// The Cypher arrow used to truncate the statement at its comment, so the delete below ran as
    /// an unfiltered <c>MATCH (a:Person) DETACH DELETE a</c> at every line terminator (#1150).
    /// It now fails at parse time on both session seams and deletes nothing.
    /// </summary>
    /// <param name="terminator">The line terminator, by name.</param>
    [Theory(DisplayName = "Cohesion Test [Graph] - Cypher arrows: --> fails with GQL0008 at every terminator and changes nothing")]
    [InlineData("LF")]
    [InlineData("CR")]
    [InlineData("CRLF")]
    [InlineData("NEL")]
    [InlineData("LS")]
    [InlineData("PS")]
    public async Task Execute_CypherArrow_ShouldFailWithoutEffectAsync(string terminator)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create(new());
        await using var session = await SeedAsync(engine, "INSERT (:Person {name: 'Alice'})-[:KNOWS]->(:Person {name: 'Bob'}), (:P {name: 'p'})");
        string separator = Terminator(terminator);
        string[] statements =
        [
            "MATCH (a:Person)-->(b:Person)" + separator + "DETACH DELETE a",
            "MATCH (b:Person)-->(a)" + separator + "RETURN b",
            "MATCH (a)--(b) RETURN a",
            "MATCH (a)-[r]--(b) RETURN a",
            "MATCH (a)-[r]-->(b) RETURN a",
            "INSERT (:P)-->(:P)",
        ];

        foreach (string gql in statements)
        {
            // Act
            var text = await Should.ThrowAsync<DatabaseParseException>(async () =>
                await session.ExecuteAsync(gql, cancellationToken: CancellationToken.None));
            var typed = await Should.ThrowAsync<DatabaseParseException>(async () =>
                await session.ExecuteAsync(new GraphQueryRequest((GqlQueryStatement)new GqlQueryParser().Parse(gql)), CancellationToken.None));

            // Assert
            text.Message.ShouldStartWith("GQL parse error GQL0008: ", Case.Sensitive);
            typed.Message.ShouldBe(text.Message);
        }
        (await ColumnAsync(session, "MATCH (n:Person) RETURN n.name")).ShouldBe(["Alice", "Bob"]);
        (await ColumnAsync(session, "MATCH (n:P) RETURN n.name")).ShouldBe(["p"]);
        (await RowsAsync(session, "MATCH ()-[r:KNOWS]->() RETURN r")).Count.ShouldBe(1);
    }

    /// <param name="gql">A tilde edge or Cypher left arrow.</param>
    /// <param name="code">The parse code it reports.</param>
    [Theory(DisplayName = "Cohesion Test [Graph] - Edges: tilde edges and Cypher left arrows fail at parse time and change nothing")]
    [InlineData("MATCH (a:Person)~[r]~(b) DETACH DELETE a", "COHDBL001")]
    [InlineData("MATCH (a:Person)<~[r]~(b) DETACH DELETE a", "COHDBL001")]
    [InlineData("MATCH (a:Person)~[r]~>(b) DETACH DELETE a", "COHDBL001")]
    [InlineData("MATCH (a:Person)~(b) DETACH DELETE a", "COHDBL001")]
    [InlineData("MATCH (a:Person)<~(b) DETACH DELETE a", "COHDBL001")]
    [InlineData("MATCH (a:Person)~>(b) DETACH DELETE a", "COHDBL001")]
    [InlineData("INSERT (:Person)~>(:Person)", "COHDBL001")]
    [InlineData("MATCH (a:Person)<--(b) DETACH DELETE a", "GQL0002")]
    [InlineData("MATCH (a:Person)<-->(b) DETACH DELETE a", "GQL0002")]
    public async Task Execute_UnsupportedEdge_ShouldFailWithoutEffectAsync(string gql, string code)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create(new());
        await using var session = await SeedAsync(engine, "INSERT (:Person {name: 'Alice'})-[:KNOWS]->(:Person {name: 'Bob'})");

        // Act
        var error = await Should.ThrowAsync<DatabaseParseException>(async () =>
            await session.ExecuteAsync(gql, cancellationToken: CancellationToken.None));

        // Assert
        error.Message.ShouldStartWith($"GQL parse error {code}: ", Case.Sensitive);
        (await ColumnAsync(session, "MATCH (n:Person) RETURN n.name")).ShouldBe(["Alice", "Bob"]);
    }

    private static async Task<IDatabaseSession> SeedAsync(GraphDatabaseEngine engine, string seed)
    {
        var database = (IGraphDatabase)await engine.CreateDatabaseAsync("labels", CancellationToken.None);
        var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync(seed, cancellationToken: CancellationToken.None);
        return session;
    }

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

    private static ValueTask<GraphPlan> PlanAsync(IGraphDatabase database, IDatabaseSession session, string query)
    {
        var instance = (GraphDatabaseInstance)database;
        return instance.RunAsync((GraphDatabaseSession)session, operation => new ValueTask<GraphPlan>(
            new GraphPlanner(instance, operation.Context.Snapshot).Plan(GraphQueryRequest.FromGql(query).Statement.GqlExpression)), CancellationToken.None);
    }

    /// <summary>The line terminators by name, so no invisible character sits in the test source.</summary>
    /// <param name="name">LF, CR, CRLF, NEL, LS or PS.</param>
    /// <returns>The terminator text.</returns>
    private static string Terminator(string name) => name switch
    {
        "LF" => "\n",
        "CR" => "\r",
        "CRLF" => "\r\n",
        "NEL" => ((char)0x0085).ToString(),
        "LS" => ((char)0x2028).ToString(),
        "PS" => ((char)0x2029).ToString(),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown line terminator."),
    };
}
