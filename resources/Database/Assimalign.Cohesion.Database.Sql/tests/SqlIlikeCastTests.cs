using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed class SqlIlikeCastTests
{
    private readonly ITestOutputHelper _output;

    public SqlIlikeCastTests(ITestOutputHelper output) { _output = output; }

    [Theory(DisplayName = "Cohesion Test [Sql] - ILIKE: every collation agrees with exhaustive per-collation scalar partitions (#1721)")]
    [InlineData("binary")]
    [InlineData("case_insensitive")]
    [InlineData("case_accent_insensitive")]
    [InlineData("invariant")]
    public void Match_InsensitivePatterns_ShouldAgreeWithOracle(string name)
    {
        // Arrange: token lists make the oracle independent of the production pattern compiler.
        var collation = Collation.FromName(name);
        string?[][] patterns = [[null], [""], [], [null, "a"], ["A", ""], ["é", null], [null, "́", null, "b"], ["%", ""], ["𐐀", null], ["A", "%"], ["a", "_"], ["Σ", null]];
        string[] scalars = ["A", "a", "é", "e", "́", "%", "𐐨", "Σ", "ς"];
        var inputs = new List<string> { "" };
        for (int depth = 0, first = 0; depth < 3; depth++)
        {
            int last = inputs.Count;
            for (int index = first; index < last; index++)
            { foreach (string scalar in scalars) { inputs.Add(inputs[index] + scalar); } }
            first = last;
        }
        int comparisons = 0;
        // Act / Assert
        foreach (var tokens in patterns)
        {
            string text = string.Concat(tokens.Select(token => token is null ? "%" : token.Length == 0 ? "_" : token.Replace("%", "X%").Replace("_", "X_")));
            var compiled = SqlLikePattern.Compile(text, "X", CancellationToken.None).Normalize(collation, ignoreCase: true);
            foreach (string input in inputs)
            {
                bool expected = Slow(0, 0);
                compiled.Match(input, collation, CancellationToken.None, out _, ignoreCase: true).ShouldBe(expected, name + ": " + text + ": " + input);
                comparisons++;

                bool Slow(int token, int start)
                {
                    if (token == tokens.Length) { return start == input.Length; }
                    if (tokens[token] is "") { return start < input.Length && Slow(token + 1, start + (char.IsSurrogatePair(input, start) ? 2 : 1)); }
                    for (int end = start; ;)
                    {
                        if ((tokens[token] is null || Equal(input[start..end], tokens[token]!)) && Slow(token + 1, end)) { return true; }
                        if (end == input.Length) { return false; }
                        end += char.IsSurrogatePair(input, end) ? 2 : 1;
                    }
                }
            }
        }
        comparisons.ShouldBe(9840);

        bool Equal(string left, string right) => name switch
        {
            "binary" => collation.Compare(left.ToLowerInvariant(), right.ToLowerInvariant()) == 0,
            "invariant" => CultureInfo.InvariantCulture.CompareInfo.Compare(left, right, CompareOptions.IgnoreCase) == 0,
            _ => collation.Compare(left, right) == 0,
        };
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Conformance: ILIKE and postfix CAST execute with inherited NULL, ESCAPE and type rules (#1721)")]
    [InlineData("'A%' ILIKE 'aX%' ESCAPE 'X'", true)]
    [InlineData("'É' COLLATE binary ILIKE 'e'", false)]
    [InlineData("'É' COLLATE case_accent_insensitive ILIKE 'e'", true)]
    [InlineData("'A' COLLATE invariant NOT ILIKE 'b'", true)]
    [InlineData("'' ILIKE ''", true)]
    [InlineData("'' ILIKE '%'", true)]
    [InlineData("NULL ILIKE '%'", null)]
    [InlineData("'x' NOT ILIKE NULL", null)]
    [InlineData("'x' ILIKE 'x' ESCAPE NULL", null)]
    [InlineData("- '7'::INT + 2 * '3'::INT", -1L)]
    [InlineData("'7'::TEXT::INT", 7)]
    [InlineData("NULL::TEXT::INT", null)]
    [InlineData("'abc'::VARCHAR(2)", "ab")]
    [InlineData("'A' ILIKE 'a' = TRUE", true)]
    public async Task Execute_OperatorCorpus_ShouldReturnExpectedAsync(string expression, object? expected)
    {
        // Arrange
        await using var engine = SqlPredicateExecutionTests.CreateEngine();
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Act / Assert
        (await SqlPredicateExecutionTests.RowsAsync(session, "SELECT " + expression)).ShouldHaveSingleItem()[0].ShouldBe(expected);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Postfix CAST: failures retain existing CAST diagnostics (#1721)")]
    [InlineData("'bad'::INT", "CS02022")]
    [InlineData("'7'::no_such_type", "CS02010")]
    [InlineData("'9999999999999999999999999'::INT", "CS02002")]
    [InlineData("'x' ILIKE 'x!' ESCAPE '!'", "CS02034")]
    public async Task Execute_InvalidOperator_ShouldRetainDiagnosticAsync(string expression, string code)
    {
        // Arrange
        await using var engine = SqlPredicateExecutionTests.CreateEngine();
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Act
        var error = await Should.ThrowAsync<DatabaseException>(() => SqlPredicateExecutionTests.RowsAsync(session, "SELECT " + expression));
        // Assert
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(code);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - ILIKE: column, ESCAPE and subquery collations bind dynamic operands (#1721)")]
    public async Task Execute_CollationCandidates_ShouldPreserveGroupingIdentityAsync()
    {
        // Arrange
        await using var engine = SqlPredicateExecutionTests.CreateEngine();
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SqlPredicateExecutionTests.ExecuteAsync(session, "CREATE TABLE candidates(s TEXT COLLATE case_accent_insensitive, p TEXT, esc TEXT)");
        await SqlPredicateExecutionTests.ExecuteAsync(session, "INSERT INTO candidates VALUES('É%', 'eX%', 'X')");
        await SqlPredicateExecutionTests.ExecuteAsync(session, "CREATE TABLE groups(s TEXT)");
        await SqlPredicateExecutionTests.ExecuteAsync(session, "INSERT INTO groups VALUES('A'),('a')");
        // Act / Assert
        (await SqlPredicateExecutionTests.RowsAsync(session, "SELECT s ILIKE p ESCAPE esc, 'É%' ILIKE p ESCAPE esc COLLATE case_accent_insensitive, 'É' ILIKE (SELECT 'e' COLLATE case_accent_insensitive) FROM candidates"))
            .ShouldHaveSingleItem().ShouldBe(new object?[] { true, true, true });
        (await SqlPredicateExecutionTests.RowsAsync(session, "SELECT s LIKE 'a', s ILIKE 'a', COUNT(*) FROM groups GROUP BY s LIKE 'a', s ILIKE 'a' ORDER BY 1"))
            .ShouldBe(new object?[][] { [false, true, 1L], [true, true, 1L] });
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - ILIKE and postfix CAST: canonical views, defaults and checks survive restart (#1721)")]
    public async Task Open_StoredOperators_ShouldReloadCanonicalDefinitionsAsync()
    {
        // Arrange
        var storage = new CrashCaptureSqlStorageStrategy();
        await using (var engine = SqlDatabaseEngine.Create("operators", new() { StorageStrategy = storage }))
        {
            var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
            await using var session = await database.CreateSessionAsync(CancellationToken.None);
            await SqlPredicateExecutionTests.ExecuteAsync(session, "CREATE FUNCTION name_matches(v TEXT) RETURNS BOOLEAN IMMUTABLE RETURN v ILIKE 'a%'");
            await SqlPredicateExecutionTests.ExecuteAsync(session, "CREATE TABLE t (v INT DEFAULT '7'::TEXT::INT, s TEXT DEFAULT 'ADA', ok BOOLEAN DEFAULT name_matches('Ada'), CHECK (s ILIKE 'a%'))");
            await SqlPredicateExecutionTests.ExecuteAsync(session, "INSERT INTO t DEFAULT VALUES");
            await SqlPredicateExecutionTests.ExecuteAsync(session, "CREATE VIEW v AS SELECT v::TEXT AS n, s ILIKE 'a%' AS matched, ok FROM t");
            (await SqlPredicateExecutionTests.RowsAsync(session, "SELECT COLUMN_DEFAULT FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME='t' AND COLUMN_NAME='v'"))
                .ShouldHaveSingleItem()[0].ShouldBe("CAST(CAST('7' AS TEXT) AS INT)");
            (await SqlPredicateExecutionTests.RowsAsync(session, "SELECT CHECK_CLAUSE FROM INFORMATION_SCHEMA.CHECK_CONSTRAINTS"))
                .ShouldHaveSingleItem()[0].ShouldBe("s ILIKE 'a%'");
        }
        // Act
        await using var reopened = SqlDatabaseEngine.Create("operators", new() { StorageStrategy = storage.CaptureDurableImages() });
        var restored = await reopened.OpenDatabaseAsync("db", CancellationToken.None);
        await using var reader = await restored.CreateSessionAsync(CancellationToken.None);
        await SqlPredicateExecutionTests.ExecuteAsync(reader, "INSERT INTO t DEFAULT VALUES");
        // Assert
        (await SqlPredicateExecutionTests.RowsAsync(reader, "SELECT * FROM v")).ShouldBe(new object?[][] { ["7", true, true], ["7", true, true] });
        await SqlConstraintAssertions.ThrowAsync(() => SqlPredicateExecutionTests.ExecuteAsync(reader, "INSERT INTO t(s) VALUES('bob')"));
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - ILIKE: invariant work and admission cancellation retain LIKE protections (#1721)")]
    [InlineData("binary")]
    [InlineData("case_insensitive")]
    [InlineData("case_accent_insensitive")]
    [InlineData("invariant")]
    public void Match_InsensitiveWork_ShouldRemainBoundedAndCancellable(string name)
    {
        // Arrange
        var collation = Collation.FromName(name);
        var pattern = SqlLikePattern.Compile(string.Concat(Enumerable.Repeat("%เก", 32)) + "ข", "!", CancellationToken.None);
        string input = string.Concat(Enumerable.Repeat("เก", 128));
        long work = 0;
        // Act / Assert
        if (name == "invariant")
        {
            var error = Should.Throw<DatabaseException>(() => pattern.Match(input, collation, CancellationToken.None, out work, ignoreCase: true));
            error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS05040");
            work.ShouldBeGreaterThan(0);
            work.ShouldBeLessThanOrEqualTo(pattern.WorkLimit(input.Length));
        }
        using var cancellation = new CancellationTokenSource();
        int entries = 0;
        Should.Throw<OperationCanceledException>(() => pattern.Match(input, collation, cancellation.Token, out _,
            () => { entries++; cancellation.Cancel(); }, ignoreCase: true));
        entries.ShouldBe(1);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - ILIKE: deterministic matcher work equals its LIKE collation path (#1721)")]
    [InlineData("binary", "case_insensitive", 1048576)]
    [InlineData("case_insensitive", "case_insensitive", 1048576)]
    [InlineData("case_accent_insensitive", "case_accent_insensitive", 8388608)]
    [InlineData("invariant", "invariant", 4194304)]
    public void Match_InsensitiveResources_ShouldRetainLikeWork(string name, string baselineName, long allocationLimit)
    {
        // Arrange
        const int length = 8192, wildcards = 2048;
        string text = string.Concat(Enumerable.Repeat("%a", wildcards)) + "b";
        string input = new('a', length);
        var collation = Collation.FromName(name);
        var baselineCollation = Collation.FromName(baselineName);
        var baseline = SqlLikePattern.Compile(text, "!", CancellationToken.None).Normalize(baselineCollation);
        var pattern = SqlLikePattern.Compile(text, "!", CancellationToken.None).Normalize(collation, ignoreCase: true);
        baseline.Match(input, baselineCollation, CancellationToken.None, out long beforeWork).ShouldBeFalse();
        long allocated = long.MaxValue, work = 0;
        // Act
        for (int round = 0; round < 3; round++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            bool matched = pattern.Match(input, collation, CancellationToken.None, out work, ignoreCase: true);
            allocated = Math.Min(allocated, GC.GetAllocatedBytesForCurrentThread() - before);
            matched.ShouldBeFalse();
        }
        // Assert
        work.ShouldBe(beforeWork);
        work.ShouldBeLessThanOrEqualTo(pattern.WorkLimit(length));
        allocated.ShouldBeLessThanOrEqualTo(allocationLimit);
        _output.WriteLine($"{name}: before work={beforeWork}; after work={work}; minimum allocation={allocated} bytes (3 rounds).");
    }
}
