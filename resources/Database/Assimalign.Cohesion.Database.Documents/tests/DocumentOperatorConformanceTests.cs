using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Documents.Tests;

public sealed class DocumentOperatorConformanceTests
{
    [Theory(DisplayName = "Cohesion Test [Documents] - IS: SQL++ operand precedence preserves the adopted results")]
    [InlineData("n = 1 IS NOT NULL", false)]
    [InlineData("1 = NULL IS NULL", false)]
    [InlineData("1 IN [1] IS NULL", null)]
    [InlineData("2 BETWEEN 1 AND 3 IS NOT NULL", false)]
    [InlineData("2 BETWEEN 1 IS NOT NULL AND 3", false)]
    [InlineData("'abc' LIKE 'a%' IS NOT NULL", null)]
    [InlineData("'a%' LIKE 'a!%' ESCAPE '!' IS NOT NULL", null)]
    public async Task ExecuteAsync_IsBoundary_ShouldReturnCouchbaseResultAsync(string expression, object? expected)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("operator-precedence", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await DocumentSubqueryTests.SeedAsync(session);
        // Act / Assert: use several source rows on each dispatch path.
        foreach (string extra in new[] { "", ", (SELECT RAW 1 FROM items LIMIT 1) AS nested" })
        {
            var rows = await DocumentSubqueryTests.RowsAsync(session, "SELECT " + expression + " AS v" + extra + " FROM items");
            rows.Count.ShouldBe(3);
            foreach (var row in rows) { row.GetValue(0).ShouldBe(expected); row.IsMissing(0).ShouldBeFalse(); }
        }
        (await DocumentSubqueryTests.RowsAsync(session, "SELECT RAW n FROM items WHERE n = 1 IS NOT NULL")).ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Concatenation: strings kinds NULL and MISSING follow CONCAT")]
    [InlineData("'a' || 'b'", "ab", false, null)]
    [InlineData("n || 'b'", null, false, null)]
    [InlineData("NULL || 'b'", null, false, null)]
    [InlineData("'a' || NULL", null, false, null)]
    [InlineData("MISSING || 'b'", null, true, null)]
    [InlineData("'a' || MISSING", null, true, null)]
    [InlineData("NULL || MISSING", null, true, null)]
    [InlineData("1 || 'b'", null, false, "CD05001")]
    [InlineData("[] || 'b'", null, false, "CD05001")]
    [InlineData("{} || 'b'", null, false, "CD05001")]
    public async Task ExecuteAsync_Concatenation_ShouldPreserveKindsAsync(string expression, string? expected, bool missing, string? code)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("concat-conformance", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await DocumentSubqueryTests.SeedAsync(session);
        // Act / Assert
        if (code is not null)
        {
            var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("SELECT " + expression + " FROM items", cancellationToken: CancellationToken.None));
            error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(code);
            session.LastQuerySubqueriesExecuted.ShouldBe(0);
            return;
        }
        var rows = await DocumentSubqueryTests.RowsAsync(session, "SELECT " + expression + " AS v FROM items");
        rows.Count.ShouldBe(3);
        foreach (var row in rows) { row.GetValue(0).ShouldBe(expected); row.IsMissing(0).ShouldBe(missing); }
    }

    public static IEnumerable<object?[]> DistinctMatrix()
    {
        string[] values = ["MISSING", "NULL", "1", "2", "'1'", "[1]", "{a:1}", "[NULL]"];
        for (int left = 0; left < values.Length; left++)
        {
            for (int right = 0; right < values.Length; right++)
            {
                // func_distinct.go:51-73,134-156 handles top-level unknowns first,
                // then SQL++ equality. A NULL element can make array equality unknown.
                object? equal = (left == 5 || left == 7) && (right == 5 || right == 7) && (left == 7 || right == 7) ? null : left == right;
                yield return [values[left], values[right], equal];
            }
        }
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Distinctness: the MISSING NULL value matrix follows func_distinct.go")]
    [MemberData(nameof(DistinctMatrix))]
    public async Task ExecuteAsync_DistinctMatrix_ShouldMatchCouchbaseAsync(string left, string right, object? equal)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("distinct-conformance", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await DocumentSubqueryTests.SeedAsync(session);
        // Act / Assert
        foreach (string extra in new[] { "", ", (SELECT RAW 1 FROM items LIMIT 1) AS nested" })
        {
            var rows = await DocumentSubqueryTests.RowsAsync(session, "SELECT " + left + " IS DISTINCT FROM " + right + " AS d, " +
                left + " IS NOT DISTINCT FROM " + right + " AS same" + extra + " FROM items");
            rows.Count.ShouldBe(3);
            foreach (var row in rows)
            {
                row.GetValue(0).ShouldBe(equal is not true);
                row.GetValue(1).ShouldBe(equal);
                row.IsMissing(0).ShouldBeFalse();
                row.IsMissing(1).ShouldBeFalse();
            }
        }
    }
}
