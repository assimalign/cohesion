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
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed class SqlOperatorFixTests
{
    private readonly ITestOutputHelper _output;

    public SqlOperatorFixTests(ITestOutputHelper output) { _output = output; }

    [Fact(DisplayName = "Cohesion Test [Sql] - Binary ILIKE: a LIKE-normalized pattern still receives lowercase mapping (#1721)")]
    public void Normalize_BinaryLikeThenIlike_ShouldLowerCachedLiterals()
    {
        // Arrange
        var pattern = SqlLikePattern.Compile("Σ", "!", CancellationToken.None).Normalize(Collation.Binary);
        // Act
        var lowered = pattern.Normalize(Collation.Binary, ignoreCase: true);
        // Assert
        lowered.Match("σ", Collation.Binary, CancellationToken.None, out _, ignoreCase: true).ShouldBeTrue();
        lowered.Match("ς", Collation.Binary, CancellationToken.None, out _, ignoreCase: true).ShouldBeFalse();
        lowered.Normalize(Collation.Binary, ignoreCase: true).ShouldBeSameAs(lowered);
        pattern.Match("Σ", Collation.Binary, CancellationToken.None).ShouldBeTrue();
        pattern.Match("σ", Collation.Binary, CancellationToken.None).ShouldBeFalse();
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Invariant ILIKE: wildcard searches retain IgnoreCase throughout the matcher (#1721)")]
    [InlineData("aΟΣ", "%ος")]
    [InlineData("ΟΣa", "ος%")]
    [InlineData("aΟΣb", "%ος%")]
    [InlineData("aΟΣb", "%ος_")]
    [InlineData("áΟΣb", "%ος_")]
    [InlineData("áΟΣb", "_%ος_")]
    public void Match_InvariantCaseVariantAnchors_ShouldIgnoreCase(string value, string pattern)
    {
        // Arrange
        var compiled = SqlLikePattern.Compile(pattern, "!", CancellationToken.None).Normalize(Collation.Invariant, ignoreCase: true);
        // Act
        bool matched = compiled.Match(value, Collation.Invariant, CancellationToken.None, out _, ignoreCase: true);
        // Assert
        matched.ShouldBeTrue();
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Binary ILIKE: follows LOWER rather than case folding (#1721)")]
    [InlineData("Σ", "ς", false)]
    [InlineData("ſ", "s", false)]
    [InlineData("É", "é", true)]
    [InlineData("ΟΣ", "οσ", true)]
    [InlineData("K", "k", true)]
    public async Task Execute_BinaryLowercase_ShouldMatchLowerFunctionAsync(string value, string pattern, bool expected)
    {
        // Arrange
        await using var engine = SqlPredicateExecutionTests.CreateEngine();
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Act
        var row = (await SqlPredicateExecutionTests.RowsAsync(session,
            $"SELECT '{value}' COLLATE binary ILIKE '{pattern}', LOWER('{value}') COLLATE binary LIKE LOWER('{pattern}')")).ShouldHaveSingleItem();
        // Assert
        row.ShouldBe(new object?[] { expected, expected });
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Binary ILIKE: oracle corpus equals LOWER(value) LIKE LOWER(pattern) (#1721)")]
    public async Task Execute_BinaryOracleCorpus_ShouldEqualLowerLikeAsync()
    {
        // Arrange: the exhaustive oracle's 820 inputs and 12 patterns, plus casing counterexamples.
        string[] scalars = ["A", "a", "é", "e", "́", "%", "𐐨", "Σ", "ς"];
        var inputs = new List<string> { "" };
        for (int depth = 0, first = 0; depth < 3; depth++)
        {
            int last = inputs.Count;
            for (int index = first; index < last; index++)
            { foreach (string scalar in scalars) { inputs.Add(inputs[index] + scalar); } }
            first = last;
        }
        inputs.AddRange(["ſ", "ΟΣ", "K", "İ", "é"]);
        string[] patterns = ["%", "_", "", "%a", "A_", "é%", "%́%b", "!%_", "𐐀%", "A!%", "a!_", "Σ%", "ς", "s", "οσ", "k", "i"];
        await using var engine = SqlPredicateExecutionTests.CreateEngine();
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SqlPredicateExecutionTests.ExecuteAsync(session, "CREATE TABLE inputs(x TEXT)");
        await SqlPredicateExecutionTests.ExecuteAsync(session, "CREATE TABLE patterns(p TEXT)");
        await SqlPredicateExecutionTests.ExecuteAsync(session, "INSERT INTO inputs VALUES " + string.Join(",", inputs.Select(value => "('" + value + "')")));
        await SqlPredicateExecutionTests.ExecuteAsync(session, "INSERT INTO patterns VALUES " + string.Join(",", patterns.Select(value => "('" + value + "')")));
        // Act
        var rows = await SqlPredicateExecutionTests.RowsAsync(session,
            "SELECT x, p, x COLLATE binary ILIKE p ESCAPE '!', LOWER(x) COLLATE binary LIKE LOWER(p) ESCAPE '!' FROM inputs CROSS JOIN patterns");
        // Assert
        rows.Count.ShouldBe(14025);
        foreach (var row in rows) { row[2].ShouldBe(row[3], $"value={row[0]}; pattern={row[1]}"); }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Invariant ILIKE: uses the linguistic comparer's IgnoreCase equality (#1721)")]
    [InlineData("ΟΣ", "ος")]
    [InlineData("Σ", "ς")]
    [InlineData("σ", "ς")]
    [InlineData("é", "é")]
    [InlineData("İ", "i")]
    public async Task Execute_InvariantVariants_ShouldMatchComparerAsync(string value, string pattern)
    {
        // Arrange
        bool expected = CultureInfo.InvariantCulture.CompareInfo.Compare(value, pattern, CompareOptions.IgnoreCase) == 0;
        bool equal = Collation.Invariant.Compare(value, pattern) == 0;
        await using var engine = SqlPredicateExecutionTests.CreateEngine();
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Act
        var row = (await SqlPredicateExecutionTests.RowsAsync(session,
            $"SELECT '{value}' COLLATE invariant ILIKE '{pattern}', '{value}' COLLATE invariant = '{pattern}', '{value}' COLLATE invariant ILIKE '_'")).ShouldHaveSingleItem();
        // Assert
        row.ShouldBe(new object?[] { expected, equal, value.EnumerateRunes().Count() == 1 });
        _output.WriteLine($"{value} / {pattern}: IgnoreCase={expected}; equality={equal}; underscore={row[2]}");
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Unary plus: postfix TEXT cast binds before the sign (#1721)")]
    [InlineData("+1::TEXT")]
    [InlineData("+ 1::TEXT")]
    [InlineData("-1::TEXT")]
    [InlineData("- 1::TEXT")]
    public async Task Execute_SignedTextCast_ShouldRefuseNumericOperatorAsync(string expression)
    {
        // Arrange
        await using var engine = SqlPredicateExecutionTests.CreateEngine();
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Act
        var error = await Should.ThrowAsync<DatabaseException>(() => SqlPredicateExecutionTests.RowsAsync(session, "SELECT " + expression));
        // Assert
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS02003");
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Unary plus: numeric casts, column casts and ordinal aliases retain results (#1721)")]
    public async Task Execute_NumericPlus_ShouldPreserveAliasesAndCastsAsync()
    {
        // Arrange
        await using var engine = SqlPredicateExecutionTests.CreateEngine();
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SqlPredicateExecutionTests.ExecuteAsync(session, "CREATE TABLE t(x TEXT)");
        await SqlPredicateExecutionTests.ExecuteAsync(session, "INSERT INTO t VALUES('2'),('1')");
        // Act
        var rows = await SqlPredicateExecutionTests.RowsAsync(session, "SELECT +x::INT, +1::INT, +1 FROM t ORDER BY +1");
        // Assert
        rows.ShouldBe(new object?[][] { [1, 1, 1L], [2, 1, 1L] });
    }
}
