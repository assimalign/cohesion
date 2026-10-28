using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;

namespace Assimalign.Cohesion.Database.Documents.Tests;

public sealed class DocumentSubqueryParityTests
{
    [Fact(DisplayName = "Cohesion Test [Documents] - Subqueries: star keeps the synchronous stored-value projection")]
    public async Task ExecuteAsync_StarWithSubqueryPredicate_ShouldPreserveStoredValueAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("star-parity", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await DocumentSubqueryTests.SeedAsync(session);
        // Act
        var expected = await DocumentSubqueryTests.RowsAsync(session, "SELECT * FROM items WHERE TRUE");
        var actual = await DocumentSubqueryTests.RowsAsync(session, "SELECT * FROM items WHERE EXISTS (SELECT RAW 1 FROM items)");
        // Assert
        actual.Select(row => ((JsonElement)row.GetValue(0)!).GetRawText()).ShouldBe(
            expected.Select(row => ((JsonElement)row.GetValue(0)!).GetRawText()));
    }

    public static IEnumerable<object[]> Expressions()
    {
        string[] forms = ["{v}", "+{v}", "-{v}", "NOT {v}", "EXISTS {v}",
            "{v} IS DISTINCT FROM NULL", "{v} IS NOT DISTINCT FROM NULL", "{v} IS NOT DISTINCT FROM [NULL]",
            "{v} || 'a'", "{v} + 1", "{v} - 1", "{v} * 2", "{v} / 0", "{v} % 0",
            "{v} = 1", "{v} != 1", "{v} < 1", "{v} <= 1", "{v} > 1", "{v} >= 1",
            "{v} AND tick(1)", "{v} OR tick(1)", "FALSE AND {v}", "TRUE OR {v}",
            "CASE WHEN {v} THEN tick(1) ELSE tick(2) END", "CASE {v} WHEN 1 THEN tick(1) ELSE tick(2) END",
            "COALESCE({v}, tick(1))", "IFNULL({v}, tick(1))", "IFMISSING({v}, tick(1))", "IFMISSINGORNULL({v}, tick(1))",
            "TYPE({v})", "tick(ARRAY_LENGTH([{v}]))", "LENGTH({v})", "ABS({v})", "CONCAT({v}, 'a')",
            "[{v}, MISSING, tick(1)]", "{a:{v}, a:MISSING, b:tick(1)}",
            "ARRAY {v} FOR x IN [1,2] WHEN x > 0 END", "FIRST {v} FOR x IN [1,2] END",
            "ANY x IN [{v}] SATISFIES x END", "EVERY x IN [{v}] SATISFIES x END",
            "1 IN {v}", "1 NOT IN {v}", "{v} IN (1, NULL, MISSING)", "{v} NOT IN (2, NULL, MISSING)",
            "{v} BETWEEN 0 AND 2", "{v} NOT BETWEEN 0 AND 2", "{v} LIKE 'a%'", "{v} NOT LIKE 'a%' ESCAPE ''",
            "{v} IS NULL", "{v} IS NOT NULL", "{v} IS MISSING", "{v} IS NOT MISSING", "{v} IS VALUED", "{v} IS NOT VALUED",
            "SUM(ARRAY_LENGTH([{v}]))", "ARRAY_AGG({v})", "COUNT(DISTINCT {v})", "MIN({v})", "MAX({v})",
            "[@deep, tick(1), {v}]", "{a:@deep, b:tick(1), c:{v}}",
            "ARRAY CASE WHEN x = 1 THEN @deep ELSE tick(1) END FOR x IN [{v},2] END"];
        string[] values = ["0", "1", "NULL", "TRUE", "FALSE", "'abc'", "[]", "[1]", "{a:1}", "[NULL]", "[1,NULL]", "[{a:1},{}]", "[[1],[]]", "[{a:[NULL]}]"];
        foreach (string form in forms)
        {
            foreach (string value in values)
            {
                // Each nested operand has an equivalent synchronous value. FIRST extracts
                // scalars; direct SELECT operands exercise the known-array planning rule.
                string control = form.StartsWith("LENGTH(", StringComparison.Ordinal) || form.StartsWith("ABS(", StringComparison.Ordinal) || form.StartsWith("CONCAT(", StringComparison.Ordinal) || form.Contains("||", StringComparison.Ordinal)
                    ? "FIRST q FOR q IN [" + value + "] END" : value;
                yield return [form.Replace("{v}", control, StringComparison.Ordinal),
                    form.Replace("{v}", "FIRST q FOR q IN (SELECT RAW " + value + " FROM items LIMIT 1) END", StringComparison.Ordinal)];
            }
            yield return [form.Replace("{v}", "[1]", StringComparison.Ordinal),
                form.Replace("{v}", "(SELECT RAW 1 FROM items LIMIT 1)", StringComparison.Ordinal)];
            yield return [form.Replace("{v}", "MISSING", StringComparison.Ordinal),
                form.Replace("{v}", "FIRST q FOR q IN (SELECT RAW 1 FROM items WHERE FALSE) END", StringComparison.Ordinal)];
        }
        yield return ["1 IS DISTINCT FROM [1]", "1 IS DISTINCT FROM (SELECT RAW 1 FROM items LIMIT 1)"];
        yield return ["NULL IS NOT DISTINCT FROM [NULL]", "NULL IS NOT DISTINCT FROM (SELECT RAW NULL FROM items LIMIT 1)"];
        yield return ["ARRAY x IS DISTINCT FROM [1] FOR x IN [1] END", "ARRAY x IS DISTINCT FROM (SELECT RAW 1 FROM items LIMIT 1) FOR x IN [1] END"];
        // Field/index navigation has a path-only AST; bind the equivalent operand locally.
        yield return ["FIRST q.a[0] FOR q IN [{a:[1]}] END", "FIRST q.a[0] FOR q IN (SELECT RAW {a:[1]} FROM items LIMIT 1) END"];
        yield return ["META().id", "CASE WHEN EXISTS (SELECT RAW 1 FROM items) THEN META().id END"];

    }

    public static IEnumerable<object[]> QueryPositions()
    {
        yield return ["SELECT n FROM items WHERE n IN [1,2,3] ORDER BY n", "SELECT n FROM items WHERE n IN (SELECT RAW n FROM items) ORDER BY n", 1];
        yield return ["SELECT n FROM items ORDER BY [1,2,3], n DESC", "SELECT n FROM items ORDER BY (SELECT RAW n FROM items), n DESC", 1];
        yield return ["SELECT COUNT(*) AS c FROM items GROUP BY [1,2,3]", "SELECT COUNT(*) AS c FROM items GROUP BY (SELECT RAW n FROM items)", 1];
        yield return ["SELECT COUNT(*) AS c FROM items HAVING EXISTS [1,2,3]", "SELECT COUNT(*) AS c FROM items HAVING EXISTS (SELECT RAW n FROM items)", 1];
        yield return ["SELECT * FROM items WHERE TRUE", "SELECT * FROM items WHERE EXISTS (SELECT RAW n FROM items)", 1];
        yield return ["SELECT *, n AS w FROM items WHERE TRUE", "SELECT *, n AS w FROM items WHERE EXISTS (SELECT RAW n FROM items)", 1];
        yield return ["SELECT * FROM items d UNNEST [1,2,3] AS v", "SELECT * FROM items d UNNEST (SELECT RAW n FROM items) AS v", 1];
        yield return ["SELECT *, 1 AS w FROM items d UNNEST [1,2,3] AS v", "SELECT *, 1 AS w FROM items d UNNEST (SELECT RAW n FROM items) AS v", 1];
        yield return ["SELECT * FROM items d LEFT UNNEST [] AS v", "SELECT * FROM items d LEFT UNNEST (SELECT RAW n FROM items WHERE FALSE) AS v", 1];
        yield return ["SELECT [NULL,{a:1},[2]] AS v FROM items", "SELECT (SELECT RAW v FROM items s UNNEST [NULL,{a:1},[2]] AS v LIMIT 3) AS v FROM items", 1];
        yield return ["SELECT [tick(1),tick(2),tick(3)] AS v FROM items", "SELECT (SELECT RAW tick(n) FROM items) AS v FROM items", 3];
        yield return ["SELECT n FROM items WHERE FALSE", "SELECT n FROM items WHERE FALSE AND EXISTS (SELECT RAW tick(n) FROM items)", 0];
        yield return ["SELECT n FROM items WHERE TRUE", "SELECT n FROM items WHERE TRUE OR EXISTS (SELECT RAW tick(n) FROM items)", 0];
        yield return ["SELECT CASE WHEN TRUE THEN n ELSE [] END AS v FROM items", "SELECT CASE WHEN TRUE THEN n ELSE (SELECT RAW tick(n) FROM items) END AS v FROM items", 0];
        yield return ["SELECT 1/0 AS v FROM items WHERE TRUE", "SELECT 1/0 AS v FROM items WHERE EXISTS (SELECT RAW n FROM items)", 1];
        yield return ["SELECT n FROM items WHERE 1/0 > 0 AND TRUE", "SELECT n FROM items WHERE 1/0 > 0 AND EXISTS (SELECT RAW n FROM items)", 0];
        yield return ["SELECT n FROM items WHERE TRUE ORDER BY 1/0", "SELECT n FROM items WHERE EXISTS (SELECT RAW n FROM items) ORDER BY 1/0", 1];
        yield return ["SELECT * FROM items d UNNEST [@deep] AS v", "SELECT * FROM items d UNNEST (SELECT RAW @deep FROM items LIMIT 1) AS v", 1];
        yield return ["SELECT *, 1 AS w FROM items d UNNEST [@deep] AS v", "SELECT *, 1 AS w FROM items d UNNEST (SELECT RAW @deep FROM items LIMIT 1) AS v", 1];
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Subqueries: clause parity includes rows locations callbacks and execution counts")]
    [MemberData(nameof(QueryPositions))]
    public async Task ExecuteAsync_QueryPositions_ShouldMatchRowsAndWorkAsync(string synchronous, string asynchronous, int subqueries)
    {
        // Arrange
        int calls = 0;
        var builder = DocumentDatabaseEngine.CreateBuilder("position-parity");
        builder.Functions.Add(DocumentScalarFunction.Create<decimal, decimal>("tick", value => { calls++; return value; }));
        await using var engine = builder.Build();
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await DocumentSubqueryTests.SeedAsync(session);
        // Act
        var expected = await EvaluateQueryAsync(synchronous);
        session.LastQuerySubqueriesExecuted.ShouldBe(0);
        var actual = await EvaluateQueryAsync(asynchronous);
        // Assert
        actual.Rows.ShouldBe(expected.Rows);
        actual.Code.ShouldBe(expected.Code);
        actual.Calls.ShouldBe(expected.Calls);
        actual.Line.ShouldBe(expected.Line);
        actual.Location.ShouldBe(expected.Location);
        // Error spans after the changed operand keep their offsets relative to the
        // identical suffix; spans before it (including 1/0) retain exact positions.
        CanonicalOffset(synchronous, asynchronous, expected.Start, true).ShouldBe(CanonicalOffset(synchronous, asynchronous, actual.Start, false));
        CanonicalOffset(synchronous, asynchronous, expected.End, true).ShouldBe(CanonicalOffset(synchronous, asynchronous, actual.End, false));
        session.LastQuerySubqueriesExecuted.ShouldBe(subqueries);

        async Task<(string[] Rows, string? Code, int? Start, int? End, int? Line, object? Location, int Calls)> EvaluateQueryAsync(string query)
        {
            calls = 0;
            try
            {
                await using var result = (DatabaseResultSet)await session.ExecuteAsync(query, DocumentSubqueryRegressionTests.DeepParameters(), CancellationToken.None);
                var rows = new List<string>();
                await foreach (var row in result.GetRowsAsync(CancellationToken.None))
                {
                    var values = new List<string>();
                    for (int column = 0; column < row.FieldCount; column++)
                    {
                        object? value = row.GetValue(column);
                        values.Add(result.Columns[column].Name + ":" + result.Columns[column].Type + ":" + (row.IsMissing(column) ? "MISSING" :
                            value is JsonElement json ? "Json:" + json.GetRawText() : value?.GetType().Name + ":" + Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)));
                    }
                    rows.Add(string.Join("|", values));
                }
                return (rows.ToArray(), null, null, null, null, null, calls);
            }
            catch (DatabaseException exception)
            {
                var diagnostic = exception.Diagnostics.Single();
                diagnostic.Start.ShouldNotBeNull();
                diagnostic.End.ShouldNotBeNull();
                return ([], diagnostic.Code, diagnostic.Start, diagnostic.End, diagnostic.Line, diagnostic.Location, calls);
            }
        }
    }

    private static int? CanonicalOffset(string control, string nested, int? offset, bool synchronous)
    {
        if (offset is null) { return null; }
        int prefix = 0;
        while (prefix < Math.Min(control.Length, nested.Length) && control[prefix] == nested[prefix]) { prefix++; }
        int suffix = 0;
        while (suffix < Math.Min(control.Length, nested.Length) - prefix && control[^(suffix + 1)] == nested[^(suffix + 1)]) { suffix++; }
        int end = (synchronous ? control.Length : nested.Length) - suffix;
        return offset <= prefix ? offset : offset >= end ? prefix + 1 + offset - end : prefix + 1;
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Subqueries: generated expressions preserve values failures and callback reachability")]
    [MemberData(nameof(Expressions))]
    public async Task ExecuteAsync_GeneratedExpression_ShouldMatchSynchronousSemanticsAsync(string synchronous, string asynchronous)
    {
        // Arrange
        int calls = 0;
        var builder = DocumentDatabaseEngine.CreateBuilder("parity-regression");
        builder.Functions.Add(DocumentScalarFunction.Create<decimal, decimal>("tick", value => { calls++; return value; }));
        await using var engine = builder.Build();
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.CreateCollectionAsync("items", CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO items (KEY, VALUE) VALUES ('a', {n:1})", cancellationToken: CancellationToken.None);
        // Act
        var expected = await EvaluateAsync(synchronous);
        var actual = await EvaluateAsync(asynchronous);
        // Assert
        actual.ShouldBe(expected);

        async Task<(string? Value, Type? RuntimeType, bool Missing, string? Code, int Calls)> EvaluateAsync(string expression)
        {
            calls = 0;
            try
            {
                await using var result = (DatabaseResultSet)await session.ExecuteAsync("SELECT " + expression + " FROM items LIMIT 1",
                    DocumentSubqueryRegressionTests.DeepParameters(), CancellationToken.None);
                await foreach (var row in result.GetRowsAsync(CancellationToken.None))
                {
                    object? value = row.GetValue(0);
                    return (value is JsonElement json ? json.GetRawText() : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture), value?.GetType(), row.IsMissing(0), null, calls);
                }
                throw new InvalidOperationException("The parity query must return one row.");
            }
            catch (DatabaseException exception)
            { return (null, null, false, exception.Diagnostics.Single().Code, calls); }
        }
    }
}
