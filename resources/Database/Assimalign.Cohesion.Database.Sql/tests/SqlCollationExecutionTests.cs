using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Types;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Tests;

/// <summary>Exercises collation resolution through live predicates, grouping, ordering and constraints.</summary>
public sealed class SqlCollationExecutionTests
{
    /// <summary>All scalar comparison forms inherit the column collation.</summary>
    /// <param name="predicate">The comparison form exercised through the SQL session.</param>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Collation: scalar comparisons use case insensitive columns")]
    [InlineData("name = 'alice'")]
    [InlineData("'alice' = name")]
    [InlineData("name IN ('alice', NULL)")]
    [InlineData("name BETWEEN 'alice' AND 'alice'")]
    [InlineData("name LIKE 'a_ic%'")]
    [InlineData("CASE name WHEN 'alice' THEN 1 ELSE 0 END = 1")]
    [InlineData("COALESCE(name, 'missing') = 'alice'")]
    [InlineData("CAST(name AS TEXT) = 'alice'")]
    public async Task Comparison_CaseInsensitiveColumn_ShouldMatchFoldedText(string predicate)
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("collation");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE names (id INT, name TEXT COLLATE case_insensitive);");
        await ExecuteAsync(session, "INSERT INTO names VALUES (1, 'Alice'), (2, 'Bob'), (3, NULL);");

        // Act / Assert
        (await RowsAsync(session, $"SELECT id FROM names WHERE {predicate};")).ShouldHaveSingleItem()[0].ShouldBe(1);
    }

    /// <summary>Explicit COLLATE overrides either operand's column, while bare strings use the database.</summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Collation: expression overrides column and database defaults")]
    public async Task Comparison_Overrides_ShouldResolveExpressionThenColumnThenDatabase()
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("collation", Collation.CaseInsensitive);
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE names (id INT, inherited TEXT, exact TEXT COLLATE binary);");
        await ExecuteAsync(session, "INSERT INTO names VALUES (1, 'Alice', 'Alice');");

        // Act / Assert
        (await RowsAsync(session, "SELECT id FROM names WHERE inherited = 'alice';")).Count.ShouldBe(1);
        (await RowsAsync(session, "SELECT id FROM names WHERE exact = 'alice';")).ShouldBeEmpty();
        (await RowsAsync(session, "SELECT id FROM names WHERE inherited = 'alice' COLLATE binary;")).ShouldBeEmpty();
        (await RowsAsync(session, "SELECT id FROM names WHERE exact = 'alice' COLLATE case_insensitive;")).Count.ShouldBe(1);
        (await RowsAsync(session, "SELECT id FROM names WHERE 'alice' COLLATE case_insensitive = exact;")).Count.ShouldBe(1);
        (await RowsAsync(session, "SELECT id FROM names WHERE 'Alice' = 'alice';")).Count.ShouldBe(1);
        (await RowsAsync(session, "SELECT id FROM names WHERE (inherited COLLATE binary) COLLATE case_insensitive = 'alice';")).ShouldBeEmpty();
        await ExecuteAsync(session, "UPDATE names SET exact = 'alice';");
        (await RowsAsync(session, "SELECT id FROM names WHERE inherited = exact;")).ShouldBeEmpty();
        (await RowsAsync(session, "SELECT id FROM names WHERE CASE WHEN exact = 'alice' THEN inherited ELSE 'none' END = 'alice';")).Count.ShouldBe(1);
    }

    /// <summary>Case and accent normalization applies consistently to equality, LIKE and null propagation.</summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Collation: composed and decomposed accents compare and match LIKE")]
    public async Task Comparison_AccentInsensitiveColumn_ShouldFoldCanonicalAccents()
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("collation");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE names (id INT, name TEXT COLLATE case_accent_insensitive);");
        await ExecuteAsync(session, "INSERT INTO names VALUES (1, 'CAFÉ'), (2, 'Café'), (3, 'cafe'), (4, NULL), (5, '𐐀');");

        // Act / Assert
        (await RowsAsync(session, "SELECT id FROM names WHERE name = 'cafe' ORDER BY id;")).Select(row => row[0]).ShouldBe(new object?[] { 1, 2, 3 });
        (await RowsAsync(session, "SELECT id FROM names WHERE name LIKE 'ca_e' ORDER BY id;")).Select(row => row[0]).ShouldBe(new object?[] { 1, 2, 3 });
        (await RowsAsync(session, "SELECT id FROM names WHERE name LIKE 'CA%' COLLATE binary ORDER BY id;")).Select(row => row[0]).ShouldBe(new object?[] { 1 });
        (await RowsAsync(session, "SELECT COUNT(*) FROM names WHERE name = NULL;")).ShouldHaveSingleItem()[0].ShouldBe(0L);
        (await RowsAsync(session, "SELECT id FROM names WHERE name LIKE '_';")).ShouldHaveSingleItem()[0].ShouldBe(5);
        (await RowsAsync(session, "SELECT id FROM names WHERE name LIKE '%𐐨';")).ShouldHaveSingleItem()[0].ShouldBe(5);
    }

    /// <summary>Equal normalized keys must reach the same hash bucket, including composite and null keys.</summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Collation: group equality and hashing combine case and accent variants")]
    public async Task GroupBy_CollatedKeys_ShouldHashEqualKeysTogether()
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("collation");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE names (name TEXT COLLATE case_insensitive, city TEXT COLLATE case_accent_insensitive);");
        await ExecuteAsync(session, "INSERT INTO names VALUES ('Alice', 'CAFÉ'), ('alice', 'Café'), ('ALICE', 'cafe'), (NULL, 'cafe'), (NULL, 'CAFÉ');");

        // Act
        var groups = await RowsAsync(session, "SELECT name, city, COUNT(*) FROM names GROUP BY name, city ORDER BY name;");
        var filtered = await RowsAsync(session, "SELECT name, COUNT(*) FROM names GROUP BY name HAVING name = 'alice';");
        var distinct = await RowsAsync(session, "SELECT DISTINCT name, city FROM names ORDER BY name;");

        // Assert
        groups.Count.ShouldBe(2);
        groups[0].ShouldBe(new object?[] { null, "cafe", 2L });
        groups[1].ShouldBe(new object?[] { "Alice", "CAFÉ", 3L });
        filtered.ShouldHaveSingleItem().ShouldBe(new object?[] { "Alice", 3L });
        distinct.Count.ShouldBe(2);
    }

    /// <summary>The binary behavior established by #1020 remains available beside collated grouping.</summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Collation: binary grouping stays distinct and COLLATE combines it")]
    public async Task GroupBy_BinaryAndExpressionOverride_ShouldPreserveBothBehaviors()
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("collation");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE names (name TEXT);");
        await ExecuteAsync(session, "INSERT INTO names VALUES ('Alice'), ('alice');");

        // Act / Assert
        (await RowsAsync(session, "SELECT name, COUNT(*) FROM names GROUP BY name ORDER BY name;")).Count.ShouldBe(2);
        (await RowsAsync(session, "SELECT name COLLATE case_insensitive, COUNT(*) FROM names GROUP BY name COLLATE case_insensitive;"))
            .ShouldHaveSingleItem().ShouldBe(new object?[] { "Alice", 2L });
        (await RowsAsync(session, "SELECT DISTINCT name FROM names;")).Count.ShouldBe(2);
        (await RowsAsync(session, "SELECT DISTINCT name COLLATE case_insensitive FROM names;")).ShouldHaveSingleItem()[0].ShouldBe("Alice");
        (await RowsAsync(session, "SELECT DISTINCT name COLLATE case_insensitive FROM names GROUP BY name;"))
            .ShouldHaveSingleItem()[0].ShouldBe("Alice");
    }

    /// <summary>Stable sorting and extrema use the same ordering as predicates and keys.</summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Collation: ordering and extrema use folded byte order")]
    public async Task OrderBy_CaseInsensitiveColumn_ShouldSortFoldedValuesStably()
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("collation");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE names (id INT, name TEXT COLLATE case_insensitive);");
        await ExecuteAsync(session, "INSERT INTO names VALUES (1, 'Zed'), (2, 'bob'), (3, 'Alice'), (4, 'alice');");

        // Act / Assert
        (await RowsAsync(session, "SELECT id FROM names ORDER BY name;")).Select(row => row[0]).ShouldBe(new object?[] { 3, 4, 2, 1 });
        (await RowsAsync(session, "SELECT id FROM names ORDER BY name COLLATE binary;")).Select(row => row[0]).ShouldBe(new object?[] { 3, 1, 4, 2 });
        (await RowsAsync(session, "SELECT MIN(name), MAX(name) FROM names;")).ShouldHaveSingleItem().ShouldBe(new object?[] { "Alice", "Zed" });
        (await RowsAsync(session, "SELECT name, COUNT(*) FROM names GROUP BY name ORDER BY name;"))
            .Select(row => row[0]).ShouldBe(new object?[] { "Alice", "bob", "Zed" });
        (await RowsAsync(session, "SELECT name AS label, COUNT(*) FROM names GROUP BY name ORDER BY label;"))
            .Select(row => row[0]).ShouldBe(new object?[] { "Alice", "bob", "Zed" });
        (await RowsAsync(session, "SELECT name COLLATE binary AS label, COUNT(*) FROM names GROUP BY name ORDER BY label;"))
            .Select(row => row[0]).ShouldBe(new object?[] { "Alice", "Zed", "bob" });
    }

    /// <summary>Collation also reaches CHECK predicates and join comparisons.</summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Collation: checks and joins use column comparison rules")]
    public async Task Comparison_CheckAndJoin_ShouldUseEffectiveCollation()
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("collation");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE names (name TEXT COLLATE case_insensitive CHECK (name = 'alice'));");
        await ExecuteAsync(session, "CREATE TABLE matches (name TEXT COLLATE case_insensitive);");
        await ExecuteAsync(session, "INSERT INTO names VALUES ('Alice');");
        await ExecuteAsync(session, "INSERT INTO matches VALUES ('alice');");

        // Act / Assert
        (await RowsAsync(session, "SELECT n.name FROM names n JOIN matches m ON n.name = m.name;"))
            .ShouldHaveSingleItem()[0].ShouldBe("Alice");
        await Should.ThrowAsync<SqlConstraintViolationException>(() => ExecuteAsync(session, "INSERT INTO names VALUES ('Bob');"));
    }

    /// <summary>Foreign key probes and reverse checks agree, and incompatible definitions fail before writes.</summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Collation: foreign key equality uses matching column collations")]
    public async Task ForeignKey_CollatedColumns_ShouldMatchReferencesAndRejectIncompatibleDefinitions()
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("collation");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE parents (name TEXT COLLATE case_insensitive PRIMARY KEY);");
        await ExecuteAsync(session, "CREATE TABLE children (name TEXT COLLATE case_insensitive REFERENCES parents(name));");
        await ExecuteAsync(session, "INSERT INTO parents VALUES ('Alice');");

        // Act / Assert
        await ExecuteAsync(session, "INSERT INTO children VALUES ('alice');");
        await Should.ThrowAsync<SqlConstraintViolationException>(() => ExecuteAsync(session, "DELETE FROM parents WHERE name = 'alice';"));
        var mismatch = await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session,
            "CREATE TABLE wrong (name TEXT COLLATE binary REFERENCES parents(name));"));
        mismatch.Message.ShouldContain("matching column collations", Case.Sensitive);
    }

    private static SqlDatabaseEngine CreateEngine()
        => SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "collation-execution" });

    private static Task<QueryResult> ExecuteAsync(IDatabaseSession session, string statement)
        => session.ExecuteAsync(statement, cancellationToken: CancellationToken.None).AsTask();

    private static async Task<List<object?[]>> RowsAsync(IDatabaseSession session, string statement)
    {
        await using var result = (await ExecuteAsync(session, statement)).ShouldBeAssignableTo<QueryResultSet>();
        var rows = new List<object?[]>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        {
            rows.Add(Enumerable.Range(0, row.FieldCount).Select(row.GetValue).ToArray());
        }
        return rows;
    }
}
