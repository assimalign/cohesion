using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Graph.Internal;
using Assimalign.Cohesion.Database.Language;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Graph.Tests;

/// <summary>
/// A read that names a label or relationship type the database does not have returns no rows and a
/// coded warning instead of failing with COHDBG002 (#1228), as Neo4j answers it with
/// <c>UnknownLabelWarning</c> or <c>UnknownRelationshipTypeWarning</c>. Since a failed statement
/// aborts the explicit transaction (#1188), the read must not fail: a probe for a label that does not
/// exist yet keeps the caller's transaction and its earlier writes.
/// </summary>
public sealed class GqlUnknownTokenWarningTests
{
    private const string seed = "INSERT (:Keep {name: 'k'})-[:LINK {name: 'l'}]->(:Keep {name: 'j'})";

    /// <summary>The issue's reproduction: a read probe inside an explicit transaction keeps the transaction.</summary>
    /// <param name="gql">A read that names an unknown label or relationship type.</param>
    /// <param name="code">The warning code it reports.</param>
    /// <param name="isolation">The isolation level of the explicit transaction.</param>
    [Theory(DisplayName = "Cohesion Test [Graph] - Unknown tokens: a read in an explicit transaction is empty, warns and keeps the transaction")]
    [InlineData("MATCH (n:Missing) RETURN n.name", GraphTokenResolver.UnknownLabelCode, IsolationLevel.Snapshot)]
    [InlineData("MATCH (n:Missing) RETURN n.name", GraphTokenResolver.UnknownLabelCode, IsolationLevel.ReadCommitted)]
    [InlineData("MATCH (a)-[r:Missing]->(b) RETURN r.name", GraphTokenResolver.UnknownRelationshipTypeCode, IsolationLevel.Snapshot)]
    [InlineData("MATCH (a)-[r:Missing]->(b) RETURN r.name", GraphTokenResolver.UnknownRelationshipTypeCode, IsolationLevel.ReadCommitted)]
    public async Task Execute_UnknownTokenInsideTransaction_ShouldWarnAndKeepTransactionAsync(string gql, string code, IsolationLevel isolation)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create(new());
        await using var session = await SeedAsync(engine);
        var database = (IGraphDatabase)session.Database;
        var transaction = await session.BeginTransactionAsync(isolation, CancellationToken.None);
        await session.ExecuteAsync("INSERT (:Pending {name: 'p'})", cancellationToken: CancellationToken.None);

        // Act
        var (rows, diagnostics) = await ReadAsync(session, gql);
        var state = transaction.State;
        var pending = await ColumnAsync(session, "MATCH (n:Pending) RETURN n.name");
        await transaction.CommitAsync(CancellationToken.None);

        // Assert
        rows.ShouldBeEmpty();
        var warning = diagnostics.ShouldNotBeNull().ShouldHaveSingleItem();
        warning.Code.ShouldBe(code);
        warning.Severity.ShouldBe(DiagnosticSeverity.Warning);
        warning.Message.ShouldNotBeNull().ShouldContain("'Missing'", Case.Sensitive);
        warning.Message.ShouldNotContain("COHDBG", Case.Sensitive);
        state.ShouldBe(TransactionState.Active);
        pending.ShouldBe(["p"]);
        transaction.State.ShouldBe(TransactionState.Committed);
        await using var observer = await database.CreateSessionAsync(CancellationToken.None);
        (await ColumnAsync(observer, "MATCH (n:Pending) RETURN n.name")).ShouldBe(["p"]);
    }

    /// <summary>
    /// Every unknown name is reported once per kind, in first-mention order: the patterns' labels,
    /// then their relationship types, then the WHERE clause's names. A labeled predicate's warning
    /// carries the predicate's span; a pattern name has no source position.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Graph] - Unknown tokens: each unknown name warns once per kind, in first-mention order")]
    public async Task Execute_SeveralUnknownNames_ShouldWarnOncePerNameAndKindAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create(new());
        await using var session = await SeedAsync(engine);
        const string gql = "MATCH (a:Missing)-[r:Gone]->(b:Missing), (c:Keep) WHERE a IS LABELED Other AND c:Missing RETURN a.name";

        // Act
        var (rows, diagnostics) = await ReadAsync(session, gql);
        var (_, sameName) = await ReadAsync(session, "MATCH (a:X)-[:X]->(b:Keep|X) RETURN a.name");

        // Assert
        rows.ShouldBeEmpty();
        diagnostics.ShouldNotBeNull().Select(item => (item.Code, Name(item))).ShouldBe(
        [
            (GraphTokenResolver.UnknownLabelCode, "Missing"),
            (GraphTokenResolver.UnknownRelationshipTypeCode, "Gone"),
            (GraphTokenResolver.UnknownLabelCode, "Other"),
        ]);
        diagnostics[0].Start.ShouldBeNull();
        diagnostics[0].Location.ShouldBeNull();
        var predicate = diagnostics[2];
        predicate.Location.ShouldBe(DiagnosticLocation.Absolute);
        gql[predicate.Start!.Value..predicate.End!.Value].ShouldBe("a IS LABELED Other");
        sameName.ShouldNotBeNull().Select(item => (item.Code, Name(item))).ShouldBe(
        [
            (GraphTokenResolver.UnknownLabelCode, "X"),
            (GraphTokenResolver.UnknownRelationshipTypeCode, "X"),
        ]);
    }

    /// <summary>A read whose every name exists reports nothing, and an invalid statement still fails.</summary>
    [Fact(DisplayName = "Cohesion Test [Graph] - Unknown tokens: known names report nothing; an invalid statement still fails")]
    public async Task Execute_KnownNamesOrInvalidStatement_ShouldNotWarnAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create(new());
        await using var session = await SeedAsync(engine);

        // Act
        var (rows, diagnostics) = await ReadAsync(session, "MATCH (a:Keep)-[r:LINK]->(b) WHERE b:Keep RETURN r.name");
        var invalid = await Should.ThrowAsync<DatabaseException>(async () =>
            await session.ExecuteAsync("MATCH (n:Missing) RETURN m.name", cancellationToken: CancellationToken.None));

        // Assert
        rows.Select(row => row[0]).ShouldBe(["l"]);
        diagnostics.ShouldBeNull();
        invalid.Message.ShouldStartWith("COHDBG001", Case.Sensitive);
    }

    /// <summary>
    /// Names resolve at the statement's snapshot: a transaction sees its own new label at once, and
    /// another session sees it only after the commit.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Graph] - Unknown tokens: a label resolves at the statement snapshot")]
    public async Task Execute_LabelDefinedLater_ShouldResolveAtTheStatementSnapshotAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create(new());
        await using var session = await SeedAsync(engine);
        var database = (IGraphDatabase)session.Database;
        await using var other = await database.CreateSessionAsync(CancellationToken.None);
        var transaction = await session.BeginTransactionAsync(CancellationToken.None);

        // Act
        var (_, before) = await ReadAsync(session, "MATCH (n:Later) RETURN n.name");
        await session.ExecuteAsync("INSERT (:Later {name: 'later'})", cancellationToken: CancellationToken.None);
        var (ownRows, own) = await ReadAsync(session, "MATCH (n:Later) RETURN n.name");
        var (otherRows, otherBeforeCommit) = await ReadAsync(other, "MATCH (n:Later) RETURN n.name");
        await transaction.CommitAsync(CancellationToken.None);
        var (committedRows, committed) = await ReadAsync(other, "MATCH (n:Later) RETURN n.name");

        // Assert
        before.ShouldNotBeNull().ShouldHaveSingleItem().Code.ShouldBe(GraphTokenResolver.UnknownLabelCode);
        ownRows.Select(row => row[0]).ShouldBe(["later"]);
        own.ShouldBeNull();
        otherRows.ShouldBeEmpty();
        otherBeforeCommit.ShouldNotBeNull().ShouldHaveSingleItem().Code.ShouldBe(GraphTokenResolver.UnknownLabelCode);
        committedRows.Select(row => row[0]).ShouldBe(["later"]);
        committed.ShouldBeNull();
    }

    /// <summary>
    /// Writes still define new labels and types. A write whose MATCH names an unknown label matches
    /// nothing, so it writes nothing, and reports no warning: Neo4j checks unresolved tokens only for
    /// a read-only query.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Graph] - Unknown tokens: a write's MATCH matches nothing without failing; writes define new names")]
    public async Task Execute_WriteNamingUnknownToken_ShouldMatchNothingWithoutFailingAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create(new());
        await using var session = await SeedAsync(engine);
        var database = (IGraphDatabase)session.Database;
        var transaction = await session.BeginTransactionAsync(CancellationToken.None);

        // Act
        var deleted = await session.ExecuteAsync("MATCH (n:Missing) DETACH DELETE n", cancellationToken: CancellationToken.None);
        var unmatched = await session.ExecuteAsync("MATCH (a:Keep)-[:Gone]->(b) INSERT (a)-[:NEVER]->(:Never)", cancellationToken: CancellationToken.None);
        var inserted = await session.ExecuteAsync("MATCH (a:Keep {name: 'k'}) INSERT (a)-[:FRESH]->(:Fresh {name: 'f'})", cancellationToken: CancellationToken.None);
        var state = transaction.State;
        await transaction.CommitAsync(CancellationToken.None);

        // Assert
        deleted.AffectedCount.ShouldBe(0);
        deleted.Diagnostics.ShouldBeNull();
        unmatched.AffectedCount.ShouldBe(0);
        unmatched.Diagnostics.ShouldBeNull();
        inserted.AffectedCount.ShouldBe(2);
        state.ShouldBe(TransactionState.Active);
        await using var observer = await database.CreateSessionAsync(CancellationToken.None);
        (await ColumnAsync(observer, "SHOW LABELS", ordinal: 2)).ShouldBe(["Fresh", "Keep"]);
        (await ColumnAsync(observer, "SHOW RELATIONSHIP TYPES", ordinal: 2)).ShouldBe(["FRESH", "LINK"]);
        (await ColumnAsync(observer, "MATCH (n:Keep) RETURN n.name")).Order().ShouldBe(["j", "k"]);
    }

    /// <summary>A path request reports the warning on its result and keeps the transaction.</summary>
    /// <param name="gql">A path read that names an unknown label or relationship type.</param>
    /// <param name="code">The warning code it reports.</param>
    [Theory(DisplayName = "Cohesion Test [Graph] - Unknown tokens: a path request is empty and warns")]
    [InlineData("MATCH (n:Missing) RETURN n", GraphTokenResolver.UnknownLabelCode)]
    [InlineData("MATCH p = (a:Keep)-[r:Missing]->(b) RETURN p", GraphTokenResolver.UnknownRelationshipTypeCode)]
    public async Task Execute_PathRequestNamingUnknownToken_ShouldWarnAsync(string gql, string code)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create(new());
        await using var session = await SeedAsync(engine);
        var transaction = await session.BeginTransactionAsync(CancellationToken.None);

        // Act
        var result = (GraphPathsQueryResult)await session.ExecuteAsync(GraphPathsQueryRequest.FromGql(gql), CancellationToken.None);
        var state = transaction.State;
        await transaction.RollbackAsync(CancellationToken.None);

        // Assert
        result.Paths.ShouldBeEmpty();
        result.Diagnostics.ShouldNotBeNull().ShouldHaveSingleItem().Code.ShouldBe(code);
        state.ShouldBe(TransactionState.Active);
    }

    /// <summary>
    /// A schema read of an unknown label is empty with the same warning and keeps the transaction;
    /// schema writes still require an existing label (COHDBG002) and, like any failed statement,
    /// abort the transaction.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Graph] - Unknown tokens: a schema read of an unknown label is empty and warns")]
    public async Task GetIndexesAsync_UnknownLabel_ShouldReturnEmptyWithWarningAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create(new());
        await using var session = await SeedAsync(engine);
        var database = (IGraphDatabase)session.Database;
        var schema = GraphSchema.Open(database, session);
        await schema.CreateIndexAsync("Keep", "by_name", "name", CancellationToken.None);
        var transaction = await session.BeginTransactionAsync(CancellationToken.None);
        await session.ExecuteAsync("INSERT (:Pending)", cancellationToken: CancellationToken.None);

        // Act
        var unknown = await schema.GetIndexesAsync("Missing", CancellationToken.None);
        var known = await schema.GetIndexesAsync("Keep", CancellationToken.None);
        await Should.ThrowAsync<ArgumentNullException>(async () => await schema.GetIndexesAsync(null!, CancellationToken.None));
        var activeAfterReads = transaction.State;
        var drop = await Should.ThrowAsync<DatabaseException>(async () => await schema.DropLabelAsync("Missing", CancellationToken.None));
        var afterWrite = transaction.State;
        await transaction.RollbackAsync(CancellationToken.None);

        // Assert
        unknown.ShouldBeEmpty();
        var warning = unknown.Diagnostics.ShouldHaveSingleItem();
        warning.Code.ShouldBe(GraphTokenResolver.UnknownLabelCode);
        warning.Severity.ShouldBe(DiagnosticSeverity.Warning);
        warning.Message.ShouldNotBeNull().ShouldContain("'Missing'", Case.Sensitive);
        known.Select(index => index.Name).ShouldBe(["by_name"]);
        known.Diagnostics.ShouldBeEmpty();
        activeAfterReads.ShouldBe(TransactionState.Active);
        drop.Message.ShouldStartWith("COHDBG002", Case.Sensitive);
        afterWrite.ShouldBe(TransactionState.Faulted);
    }

    /// <summary>
    /// A pattern that requires an unknown name (a node conjunction, a relationship type) plans to
    /// read nothing; one that names it only under <c>!</c>, <c>|</c> or in WHERE still reads.
    /// </summary>
    /// <param name="gql">The statement to plan.</param>
    /// <param name="matchesNothing">Whether its plan reads nothing.</param>
    [Theory(DisplayName = "Cohesion Test [Graph] - Unknown tokens: only a required unknown name empties the plan")]
    [InlineData("MATCH (n:Missing) RETURN n.name", true)]
    [InlineData("MATCH (n:Keep&Missing) RETURN n.name", true)]
    [InlineData("MATCH (n:Keep:Missing) RETURN n.name", true)]
    [InlineData("MATCH (a:Keep)-[r:Missing]->(b) RETURN a.name", true)]
    [InlineData("MATCH (a:Keep), (b:Missing) RETURN a.name", true)]
    [InlineData("MATCH (n:!Missing) RETURN n.name", false)]
    [InlineData("MATCH (n:Keep|Missing) RETURN n.name", false)]
    [InlineData("MATCH (a)-[r:LINK|Missing]->(b) RETURN a.name", false)]
    [InlineData("MATCH (a)-[r:!Missing]->(b) RETURN a.name", false)]
    [InlineData("MATCH (n) WHERE n:Missing RETURN n.name", false)]
    [InlineData("MATCH (n:Keep) RETURN n.name", false)]
    public async Task Plan_RequiredUnknownName_ShouldMatchNothingAsync(string gql, bool matchesNothing)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create(new());
        await using var session = await SeedAsync(engine);
        var database = (IGraphDatabase)session.Database;

        // Act
        var plan = await PlanAsync(database, session, gql);

        // Assert
        plan.MatchesNothing.ShouldBe(matchesNothing);
    }

    /// <summary>A name of any length is quoted in the warning cut to a readable length.</summary>
    [Fact(DisplayName = "Cohesion Test [Graph] - Unknown tokens: a long unknown name is cut in the warning")]
    public async Task Execute_LongUnknownName_ShouldCutTheQuotedNameAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create(new());
        await using var session = await SeedAsync(engine);
        string name = new('L', 10_000);

        // Act
        var (rows, diagnostics) = await ReadAsync(session, $"MATCH (n:{name}) RETURN n.name");

        // Assert
        rows.ShouldBeEmpty();
        string message = diagnostics.ShouldNotBeNull().ShouldHaveSingleItem().Message.ShouldNotBeNull();
        message.ShouldContain($"'{new string('L', 256)}...'", Case.Sensitive);
        message.Length.ShouldBeLessThan(512);
    }

    private static string Name(Diagnostic diagnostic)
    {
        string message = diagnostic.Message.ShouldNotBeNull();
        int start = message.IndexOf('\'', StringComparison.Ordinal) + 1;
        return message[start..message.IndexOf('\'', start)];
    }

    private static async Task<IDatabaseSession> SeedAsync(GraphDatabaseEngine engine)
    {
        var database = (IGraphDatabase)await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync(seed, cancellationToken: CancellationToken.None);
        return session;
    }

    private static async Task<(List<object?[]> Rows, IReadOnlyList<Diagnostic>? Diagnostics)> ReadAsync(IDatabaseSession session, string gql)
    {
        await using var result = (QueryResultSet)await session.ExecuteAsync(gql, cancellationToken: CancellationToken.None);
        var rows = new List<object?[]>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        {
            rows.Add(Enumerable.Range(0, row.FieldCount).Select(row.GetValue).ToArray());
        }
        return (rows, result.Diagnostics);
    }

    private static async Task<List<string?>> ColumnAsync(IDatabaseSession session, string gql, int ordinal = 0)
        => (await ReadAsync(session, gql)).Rows.Select(row => (string?)row[ordinal]).ToList();

    private static ValueTask<GraphPlan> PlanAsync(IGraphDatabase database, IDatabaseSession session, string gql)
    {
        var instance = (GraphDatabaseInstance)database;
        return instance.RunAsync((GraphDatabaseSession)session, operation => new ValueTask<GraphPlan>(
            new GraphPlanner(instance, operation.Context.Snapshot).Plan(GraphQueryRequest.FromGql(gql).Statement.GqlExpression)), CancellationToken.None);
    }
}
