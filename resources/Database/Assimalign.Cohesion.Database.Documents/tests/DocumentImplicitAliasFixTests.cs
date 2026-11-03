using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Documents.Internal;
using Assimalign.Cohesion.Database.Documents.Language;

namespace Assimalign.Cohesion.Database.Documents.Tests;

public sealed class DocumentImplicitAliasFixTests
{
    [Theory(DisplayName = "Cohesion Test [Documents] - Implicit aliases: inferred UNNEST names keep bare FROM keyspace-only")]
    [InlineData("", "", "3;3")]
    [InlineData(" AS tags", "", "1;1")]
    [InlineData(" tags", "", "1;1")]
    [InlineData("", " GROUP BY 1", "3")]
    public async Task ExecuteAsync_UnnestNameProvenance_ShouldDistinguishCorrelationAsync(string alias, string grouping, string expected)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("unnest-provenance", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);
        // Act
        var rows = await DocumentSubqueryTests.RowsAsync(session,
            "SELECT RAW FIRST v FOR v IN (SELECT RAW COUNT(*) FROM tags) END FROM items UNNEST tags" + alias + grouping);
        // Assert
        string.Join(';', rows.Select(row => row.GetValue(0))).ShouldBe(expected);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Implicit aliases: inferred expression-source names keep bare FROM keyspace-only")]
    [InlineData("", "", "[3,3]")]
    [InlineData(" AS tags", "", "[1,1]")]
    [InlineData("", " GROUP BY 1", "[3]")]
    public async Task ExecuteAsync_ExpressionNameProvenance_ShouldDistinguishCorrelationAsync(string alias, string grouping, string expected)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("from-provenance", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);
        // Act
        var rows = await DocumentSubqueryTests.RowsAsync(session,
            "SELECT RAW (SELECT RAW FIRST v FOR v IN (SELECT RAW COUNT(*) FROM tags) END FROM d.tags" + alias + grouping + ") FROM items d");
        // Assert
        rows.ShouldHaveSingleItem().GetValue(0).ShouldBeOfType<JsonElement>().GetRawText().ShouldBe(expected);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Implicit aliases: UPDATE access never loses rows to a self-named field index")]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ExecuteAsync_QualifiedUpdate_ShouldMatchScanAsync(bool indexed, bool nested)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("update-access", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE COLLECTION items", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO items (KEY, VALUE) VALUES ('a', {g:1,items:{g:9}})", cancellationToken: CancellationToken.None);
        if (indexed) { await session.ExecuteAsync("CREATE INDEX ix ON items(items.g)", cancellationToken: CancellationToken.None); }
        // Act
        var result = await session.ExecuteAsync("UPDATE items SET g = 5 WHERE items.g = 1" +
            (nested ? " AND EXISTS (SELECT RAW 1 FROM [1] AS s)" : ""), cancellationToken: CancellationToken.None);
        // Assert
        result.AffectedCount.ShouldBe(1);
        (await DocumentSubqueryTests.RowsAsync(session, "SELECT RAW items.g FROM items")).ShouldHaveSingleItem().GetValue(0).ShouldBe(5m);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Implicit aliases: UPDATE and DELETE EXPLAIN retain the qualified status seek")]
    [InlineData("UPDATE items SET g = 5", false)]
    [InlineData("UPDATE items SET g = 5", true)]
    [InlineData("DELETE FROM items", false)]
    [InlineData("DELETE FROM items", true)]
    public async Task ExecuteAsync_QualifiedMutationExplain_ShouldSeekStatusIndexAsync(string statement, bool nested)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("mutation-explain", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);
        await session.ExecuteAsync("CREATE INDEX by_status ON items(status)", cancellationToken: CancellationToken.None);
        // Act
        var rows = await DocumentSubqueryTests.RowsAsync(session, "EXPLAIN " + statement + " WHERE items.status = 'x'" +
            (nested ? " AND EXISTS (SELECT RAW 1 FROM [1] AS s)" : ""));
        // Assert
        rows.ShouldContain(row => row.GetString(2) == "IndexSeek" && row.GetString(4) == "by_status");
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Implicit aliases: index DDL keeps keys relative to the document")]
    [InlineData("g", "items.g = 1", "g", "a")]
    [InlineData("items.g", "items.items.g = 9", "items.g", "a")]
    [InlineData("['items'].g", "items.items.g = 9", "items.g", "a")]
    [InlineData("DISTINCT ARRAY v FOR v IN tags END", "ANY v IN items.tags SATISFIES v = 'x' END", "DISTINCT ARRAY v FOR v IN \"tags\" END", "a")]
    [InlineData("DISTINCT ARRAY v FOR v IN items.tags END", "ANY v IN items.items.tags SATISFIES v = 'z' END", "DISTINCT ARRAY v FOR v IN \"items\".tags END", "a")]
    public async Task ExecuteAsync_DocumentRelativeIndexDdl_ShouldPreserveStoredKeysAsync(string key, string predicate, string canonical, string expected)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("index-ddl-alias", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);
        await session.ExecuteAsync("CREATE INDEX ix ON items(" + key + ")", cancellationToken: CancellationToken.None);
        // Act
        var plan = await DocumentSubqueryTests.RowsAsync(session, "EXPLAIN SELECT RAW META().id FROM items WHERE " + predicate);
        var rows = await DocumentSubqueryTests.RowsAsync(session, "SELECT RAW META().id FROM items WHERE " + predicate);
        var metadata = await DocumentSubqueryTests.RowsAsync(session, "SELECT RAW PATH FROM COHESION_SCHEMA.INDEXES WHERE INDEX_NAME = 'ix'");
        // Assert
        plan.ShouldContain(row => row.GetString(2) == "IndexSeek" && row.GetString(4) == "ix");
        rows.ShouldHaveSingleItem().GetString(0).ShouldBe(expected);
        metadata.ShouldHaveSingleItem().GetString(0).ShouldBe(canonical);
        await session.ExecuteAsync("CREATE INDEX copy ON items(" + canonical + ")", cancellationToken: CancellationToken.None);
        (await DocumentSubqueryTests.RowsAsync(session, "SELECT RAW PATH FROM COHESION_SCHEMA.INDEXES ORDER BY INDEX_NAME"))
            .Select(row => row.GetString(0)).ShouldBe(new[] { canonical, canonical });
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Implicit aliases: every system source shares parser duplicate detection")]
    public void Parse_SystemSourceDuplicate_ShouldUseLanguageDiagnostic()
    {
        // Arrange
        var constants = typeof(DocumentSystemCollections).GetFields(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string)).ToArray();
        constants.ShouldNotBeEmpty();
        foreach (var field in constants)
        {
            string source = (string)field.GetRawConstantValue()!;
            string variable = source[(source.LastIndexOf('.') + 1)..];
            // Act
            var statement = new OqlQueryParser().Parse("SELECT * FROM " + source + " UNNEST x " + variable);
            // Assert
            statement.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "CD00010", source);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Implicit aliases: a system name is resolved once into its logical plan")]
    public void CreateLogicalPlan_SystemSource_ShouldReuseResolvedName()
    {
        // Arrange
        var statement = (OqlQueryStatement)new OqlQueryParser().Parse("SELECT INDEXES.INDEX_NAME FROM COHESION_SCHEMA.INDEXES GROUP BY INDEX_NAME");
        // Act
        var plan = DocumentPlanner.CreateLogicalPlan((OqlSelectExpression)statement.OqlExpression);
        string first = DocumentSourceName.Of(plan.Query);
        string second = DocumentSourceName.Of(plan.Query);
        // Assert
        first.ShouldBe("INDEXES");
        ReferenceEquals(first, second).ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Implicit aliases: joined META diagnoses its receiver argument")]
    public async Task ExecuteAsync_JoinedMetadataWithoutReceiver_ShouldNameRequiredArgumentAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("metadata-receiver", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);
        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(
            "SELECT RAW META().id FROM items JOIN tags ON META(tags).id = 'x'", cancellationToken: CancellationToken.None));
        // Assert
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD01035");
        error.Diagnostics[0].Message!.ShouldContain("receiver naming a FROM, JOIN or NEST source, and id or version", Case.Sensitive);
        (await DocumentSubqueryTests.RowsAsync(session, "SELECT RAW META(items).id FROM items JOIN tags ON META(tags).id = 'x'"))
            .ShouldHaveSingleItem().GetString(0).ShouldBe("a");
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Implicit aliases: direct DML ASTs refuse empty explicit aliases")]
    [InlineData("UPDATE")]
    [InlineData("DELETE")]
    [InlineData("INSERT")]
    [InlineData("UPSERT")]
    public async Task ExecuteAsync_EmptyDmlAlias_ShouldRefuseDuringPlanningAsync(string verb)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("empty-dml-alias", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);
        OqlExpression expression = verb switch
        {
            "UPDATE" => new OqlUpdateExpression("items", "", [new OqlUpdateAssignment(new OqlPathExpression([new("g", null)]), new OqlLiteralExpression(2m))], [], null),
            "DELETE" => new OqlDeleteExpression("items", "", null),
            "INSERT" => new OqlInsertExpression("items", "", [(new OqlLiteralExpression("k"), new OqlLiteralExpression(2m))], null, null, null),
            _ => new OqlUpsertExpression("items", "", [(new OqlLiteralExpression("k"), new OqlLiteralExpression(2m))], null, null, null),
        };
        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(
            new DocumentQueryRequest(new OqlQueryStatement(expression)), CancellationToken.None));
        // Assert
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD01023");
        (await DocumentSubqueryTests.RowsAsync(session, "SELECT RAW items.g FROM items")).ShouldHaveSingleItem().GetValue(0).ShouldBe(1m);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Implicit aliases: EXPLAIN names whole-source ordering")]
    [InlineData("items", "")]
    [InlineData("d", " d")]
    public async Task ExecuteAsync_WholeSourceOrdering_ShouldExplainNameAsync(string name, string alias)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("whole-source-order", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);
        // Act
        var rows = await DocumentSubqueryTests.RowsAsync(session, "EXPLAIN SELECT RAW " + name + " FROM items" + alias + " ORDER BY " + name);
        // Assert
        rows.ShouldContain(row => row.GetString(5) != null && row.GetString(5)!.Contains("ordering=" + name + " ASC", StringComparison.Ordinal));
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Implicit aliases: VALUES subqueries cannot capture a destination without a source row")]
    [InlineData("INSERT", "", "(SELECT RAW items.g FROM [1] AS s)")]
    [InlineData("UPSERT", "", "(SELECT RAW items.g FROM [1] AS s)")]
    [InlineData("INSERT", " AS d", "(SELECT RAW d.g FROM [1] AS s)")]
    [InlineData("UPSERT", " AS d", "(SELECT RAW META(d).id FROM [1] AS s)")]
    [InlineData("INSERT", "", "(SELECT RAW (SELECT RAW items.g FROM [1] AS t) FROM [1] AS s)")]
    [InlineData("INSERT", " AS d", "(SELECT RAW COUNT(*) FROM d)")]
    [InlineData("UPSERT", " AS d", "(SELECT RAW COUNT(*) FROM d)")]
    [InlineData("INSERT", " AS d", "(SELECT RAW (SELECT RAW COUNT(*) FROM d) FROM [1] AS s)")]
    [InlineData("UPSERT", " AS d", "(SELECT RAW (SELECT RAW COUNT(*) FROM d) FROM [1] AS s)")]
    public async Task ExecuteAsync_ValuesDestinationCapture_ShouldRefuseDuringPlanningAsync(string verb, string alias, string value)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("values-capture", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);
        await session.ExecuteAsync("CREATE COLLECTION d", cancellationToken: CancellationToken.None);
        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(verb + " INTO items" + alias +
            " (KEY, VALUE) VALUES ('k', {v:" + value + "})", cancellationToken: CancellationToken.None));
        // Assert
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD01036");
        error.Diagnostics[0].Start.ShouldNotBeNull();
        session.LastQuerySubqueriesExecuted.ShouldBe(0);
        (await DocumentSubqueryTests.RowsAsync(session, "SELECT RAW META().id FROM items")).ShouldHaveSingleItem().GetString(0).ShouldBe("a");
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Implicit aliases: VALUES subqueries retain local bindings and destination collection reads")]
    [InlineData("FIRST v FOR v IN (SELECT RAW items.g FROM [{g:7}] AS items) END", 7)]
    [InlineData("FIRST v FOR v IN (SELECT RAW items.g FROM items) END", 1)]
    [InlineData("ARRAY FIRST v FOR v IN (SELECT RAW items.g FROM [1] AS s) END FOR items IN [{g:7}] END", null)]
    public async Task ExecuteAsync_ValuesLocalBinding_ShouldRemainValidAsync(string value, int? expected)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("values-local", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);
        // Act
        var result = await session.ExecuteAsync("INSERT INTO items (KEY, VALUE) VALUES ('k', {v:" + value + "})", cancellationToken: CancellationToken.None);
        // Assert
        result.AffectedCount.ShouldBe(1);
        var rows = await DocumentSubqueryTests.RowsAsync(session, "SELECT RAW items.v FROM items WHERE META().id = 'k'");
        if (expected is int number) { rows.ShouldHaveSingleItem().GetValue(0).ShouldBe((decimal)number); }
        else { rows.ShouldHaveSingleItem().GetValue(0).ShouldBeOfType<JsonElement>().GetRawText().ShouldBe("[7]"); }
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Implicit aliases: direct VALUES paths keep their context diagnostic beside subqueries")]
    [InlineData("INSERT", false)]
    [InlineData("INSERT", true)]
    [InlineData("UPSERT", false)]
    [InlineData("UPSERT", true)]
    public async Task ExecuteAsync_DirectValuesDestinationPath_ShouldKeepContextDiagnosticAsync(string verb, bool nested)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("values-context", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);
        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(verb +
            " INTO items (KEY, VALUE) VALUES ('k', {v:items.g" +
            (nested ? ",w:(SELECT RAW 1 FROM [1] AS s)" : "") + "})", cancellationToken: CancellationToken.None));
        // Assert
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD05001");
        session.LastQuerySubqueriesExecuted.ShouldBe(0);
        (await DocumentSubqueryTests.RowsAsync(session, "SELECT RAW META().id FROM items")).ShouldHaveSingleItem().GetString(0).ShouldBe("a");
    }

    private static async Task SeedAsync(DocumentDatabaseSession session)
    {
        await session.ExecuteAsync("CREATE COLLECTION items", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE COLLECTION tags", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO items (KEY, VALUE) VALUES ('a', {g:1,status:'x',tags:['x','y'],items:{g:9,tags:['z']}})", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO tags (KEY, VALUE) VALUES ('x',1),('y',2),('z',3)", cancellationToken: CancellationToken.None);
    }
}
