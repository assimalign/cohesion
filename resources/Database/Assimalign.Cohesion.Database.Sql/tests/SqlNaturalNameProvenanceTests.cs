using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Catalog;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed class SqlNaturalNameProvenanceTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - Views refuse synthesized NATURAL inputs before freezing an incorrect product (#1792)")]
    [InlineData(false)] [InlineData(true)]
    public async Task CreateView_SynthesizedNaturalInput_ShouldRefuseBeforePublication(bool reverse)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("natural-view-refusal", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SetupAsync(session);
        const string unnamed = "(SELECT CAST(id AS BIGINT) FROM a) d";
        string from = reverse ? "b NATURAL JOIN " + unnamed : unnamed + " NATURAL JOIN b";
        // Act
        var failure = await FailureAsync(session, "CREATE VIEW bad AS SELECT * FROM " + from);
        // Assert
        failure.Diagnostics.ShouldContain(d => d.Code == "CS01015");
        failure.Message.ShouldContain("alias");
        database.Catalog.Views.ShouldNotContain(view => view.Name == "bad");
    }

    public static IEnumerable<object[]> Shapes()
    {
        foreach (string shape in new[] { "derived", "cte", "view", "star", "qualified", "compound", "view-star", "recursive", "correlated" })
        {
            yield return [shape, false];
            yield return [shape, true];
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - NATURAL names: synthesized names refuse through every query boundary and direction (#1792)")]
    [MemberData(nameof(Shapes))]
    public async Task Execute_SynthesizedNaturalInput_ShouldRequestAlias(string shape, bool reverse)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("natural-name", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SetupAsync(session);
        string prefix = shape == "cte" ? "WITH c AS (SELECT CAST(id AS BIGINT) FROM a) "
            : shape == "recursive" ? "WITH RECURSIVE c AS (SELECT CAST(1 AS BIGINT) UNION ALL SELECT column1+1 FROM c WHERE column1<2) " : "";
        string source = shape switch
        {
            "cte" or "recursive" => "c",
            "view" => "v",
            "view-star" => "vs",
            "star" => "(SELECT * FROM (SELECT CAST(id AS BIGINT) FROM a) x) d",
            "qualified" => "(SELECT x.* FROM (SELECT CAST(id AS BIGINT) FROM a) x) d",
            "compound" => "(SELECT CAST(id AS BIGINT) FROM a UNION ALL SELECT id FROM a) d",
            "correlated" => "(SELECT a.id) d",
            _ => "(SELECT CAST(id AS BIGINT) FROM a) d",
        };
        string from = reverse ? "b NATURAL JOIN " + source : source + " NATURAL JOIN b";
        // Act
        string sql = shape == "correlated" ? "SELECT (SELECT COUNT(*) FROM " + from + ") FROM a"
            : prefix + "SELECT * FROM " + from;
        var failure = await FailureAsync(session, sql);
        // Assert
        failure.Diagnostics.ShouldContain(d => d.Code == "CS01015");
        failure.Message.ShouldContain("column1");
        failure.Message.ShouldContain("alias");
        await using var unchanged = (await session.ExecuteAsync("SELECT CAST(id AS BIGINT) FROM a", cancellationToken: CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();
        unchanged.Columns.Single().Name.ShouldBe("column1");
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - NATURAL names: the review's duplicate unnamed expressions refuse in either direction (#1792)")]
    [InlineData(false)] [InlineData(true)]
    public async Task Execute_UnnamedExpressionPair_ShouldRefuseSynthesizedComparison(bool reverse)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("natural-expressions", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecAsync(session, "CREATE TABLE t(p INT,q INT); CREATE TABLE u(r INT); INSERT INTO t VALUES(1,2); INSERT INTO u VALUES(1)");
        const string left = "(SELECT p+1,q+1 FROM t) x";
        const string right = "(SELECT r+1 FROM u) y";
        // Act / Assert
        var failure = await FailureAsync(session,
            "SELECT * FROM " + (reverse ? right + " NATURAL JOIN " + left : left + " NATURAL JOIN " + right));
        failure.Diagnostics.ShouldContain(d => d.Code == "CS01015");
        failure.Message.ShouldContain("alias");
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Correlated NATURAL inputs: explicit names and literal column1 remain authored (#1792)")]
    [InlineData(false)] [InlineData(true)]
    public async Task Execute_CorrelatedAuthoredNames_ShouldJoinWithoutRenamingOutputs(bool reverse)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("natural-correlated-names", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SetupAsync(session);
        // Act / Assert
        foreach (string source in new[] { "(SELECT a.id AS id) d", "(SELECT a.id) d(id)" })
        {
            string from = reverse ? "b NATURAL JOIN " + source : source + " NATURAL JOIN b";
            await using var result = (await session.ExecuteAsync("SELECT (SELECT COUNT(*) FROM " + from + ") AS n FROM a ORDER BY a.id", cancellationToken: CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();
            var values = new List<object?>();
            await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { values.Add(row.GetValue(0)); }
            values.ShouldBe([1L, 1L]);
        }
        string literal = reverse ? "literal b NATURAL JOIN (SELECT l.column1) d" : "(SELECT l.column1) d NATURAL JOIN literal b";
        await using var named = (await session.ExecuteAsync("SELECT (SELECT COUNT(*) FROM " + literal + ") FROM literal l", cancellationToken: CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();
        var counts = new List<object?>();
        await foreach (var row in named.GetRowsAsync(CancellationToken.None)) { counts.Add(row.GetValue(0)); }
        counts.ShouldBe([1L, 1L]);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - NATURAL names: authored aliases and literal column1 identifiers remain usable (#1792)")]
    [InlineData("(SELECT CAST(id AS BIGINT) AS id FROM a) d", "", false)]
    [InlineData("(SELECT CAST(id AS BIGINT) FROM a) d(id)", "", false)]
    [InlineData("c", "WITH c(id) AS (SELECT CAST(id AS BIGINT) FROM a) ", false)]
    [InlineData("va", "", false)]
    [InlineData("vl", "", false)]
    [InlineData("literal", "", true)]
    [InlineData("vc", "", true)]
    [InlineData("vr", "", true)]
    public async Task Execute_AuthoredNaturalInput_ShouldJoinByName(string source, string prefix, bool column1)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("natural-authored", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SetupAsync(session);
        // Act / Assert: aliases work on both input sides, including a column literally named column1.
        string other = column1 ? "literal other" : "b";
        foreach (string from in new[] { source + " NATURAL JOIN " + other, other + " NATURAL JOIN " + source })
        {
            await using var result = (await session.ExecuteAsync(prefix + "SELECT * FROM " + from, cancellationToken: CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();
            int rows = 0;
            await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { rows++; }
            rows.ShouldBe(2);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - NATURAL names: synthesized view outputs and explicit view lists retain provenance through reopen (#1792)")]
    public async Task Open_ViewOutputNames_ShouldRecoverProvenanceWithoutFormatChange()
    {
        // Arrange
        string root = Path.Combine(AppContext.BaseDirectory, "natural-names-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var engine = SqlDatabaseEngine.Create("natural-names", new() { RootPath = root }))
            {
                var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
                await using var session = await database.CreateSessionAsync(CancellationToken.None);
                await SetupAsync(session);
                // A format-37 body frozen by 19dd61626 used a same-name alias for an expanded star.
                var source = database.Catalog.Views.Single(view => view.Name == "vs");
                string oldBody = source.Definition.Contains(" AS column1", StringComparison.Ordinal) ? source.Definition
                    : source.Definition.Replace("SELECT d.column1 FROM", "SELECT d.column1 AS column1 FROM", StringComparison.Ordinal);
                oldBody.ShouldContain(" AS column1", Case.Sensitive);
                await database.Catalog.CreateViewAsync(new SqlCatalogView("dbo", "old_star", oldBody, source.Columns,
                    source.TableDependencies, source.Dependencies, source.FunctionCalls), cancellationToken: CancellationToken.None);
            }
            // Act
            await using var reopened = SqlDatabaseEngine.Create("natural-names", new() { RootPath = root });
            var restored = await reopened.OpenDatabaseAsync("db", CancellationToken.None);
            await using var reader = await restored.CreateSessionAsync(CancellationToken.None);
            // Assert
            foreach (string view in new[] { "v", "vs", "old_star" })
            {
                foreach (string from in new[] { view + " NATURAL JOIN b", "b NATURAL JOIN " + view })
                {
                    var failure = await FailureAsync(reader, "SELECT * FROM " + from);
                    failure.Diagnostics.ShouldContain(d => d.Code == "CS01015");
                    failure.Message.ShouldContain("alias");
                }
            }
            await using var authored = (await reader.ExecuteAsync("SELECT * FROM vc NATURAL JOIN literal", cancellationToken: CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();
            authored.Columns.Single().Name.ShouldBe("column1");
            int count = 0;
            await foreach (var row in authored.GetRowsAsync(CancellationToken.None)) { count++; }
            count.ShouldBe(2);
        }
        finally { if (Directory.Exists(root)) { Directory.Delete(root, true); } }
    }

    private static Task SetupAsync(SqlDatabaseSession session) => ExecAsync(session,
        "CREATE TABLE a(id INT); CREATE TABLE b(id BIGINT,v INT); CREATE TABLE literal(column1 BIGINT); INSERT INTO a VALUES(1),(2); INSERT INTO b VALUES(1,10),(2,20); INSERT INTO literal VALUES(1),(2);" +
        "CREATE VIEW v AS SELECT CAST(id AS BIGINT) FROM a; CREATE VIEW vs AS SELECT d.* FROM (SELECT CAST(id AS BIGINT) FROM a) d;" +
        "CREATE VIEW va AS SELECT CAST(id AS BIGINT) AS id FROM a; CREATE VIEW vl(id) AS SELECT CAST(id AS BIGINT) FROM a; CREATE VIEW vc(column1) AS SELECT CAST(id AS BIGINT) FROM a;" +
        "CREATE VIEW vr AS SELECT column1 AS column1 FROM v");

    private static async Task ExecAsync(SqlDatabaseSession session, string sql)
    {
        foreach (string part in sql.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        { (await session.ExecuteAsync(part, cancellationToken: CancellationToken.None)).Status.ShouldBe(DatabaseResultStatus.Success); }
    }

    private static Task<DatabaseException> FailureAsync(SqlDatabaseSession session, string sql)
        => Should.ThrowAsync<DatabaseException>(async () =>
        {
            // Dispose an unexpected successful result too, so regression replay can safely fail on the base.
            await using var unexpected = (await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None)) as DatabaseResultSet;
        });
}
