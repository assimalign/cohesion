using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed class SqlJsonReviewFixTests
{
    private readonly ITestOutputHelper _output;

    public SqlJsonReviewFixTests(ITestOutputHelper output) { _output = output; }

    [Theory(DisplayName = "Cohesion Test [Sql] - JSON binding: common alternatives choose one overload (#1722)")]
    [InlineData("'[10,20]'::JSON ->> CASE WHEN FALSE THEN 1 ELSE '1' END", "20")]
    [InlineData("'[10,20]'::JSON ->> COALESCE(NULL::INT, '1')", "20")]
    [InlineData("'[10,20]'::JSON ->> NULLIF(COALESCE(NULL::INT,'1'),0)", "20")]
    [InlineData("'[10,20]'::JSON ->> CASE WHEN FALSE THEN @index ELSE '1' END", "20")]
    [InlineData("'[10,20]'::JSON ->> CASE WHEN FALSE THEN 0 ELSE CASE WHEN FALSE THEN 1 ELSE '1' END END", "20")]
    [InlineData("'[10,20]'::JSON ->> CASE WHEN TRUE THEN NULL ELSE NULL END", null)]
    [InlineData("'[10,20]'::JSON ->> COALESCE(NULL,NULL)", null)]
    [InlineData("(CASE WHEN FALSE THEN '{}'::JSON ELSE '{\"k\":20}' END)->>'k'", "20")]
    [InlineData("COALESCE(NULL::JSONB,'{\"k\":20}')->>'k'", "20")]
    [InlineData("(CASE WHEN FALSE THEN '{}'::JSONB ELSE '{}' END) IS JSON OBJECT", true)]
    public async Task Execute_CommonAlternatives_ShouldUseBoundTypeAsync(string expression, object? expected)
    {
        // Arrange
        await using var engine = SqlPredicateExecutionTests.CreateEngine();
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var request = SqlQueryRequest.FromSql("SELECT " + expression, new Dictionary<string, object?> { ["index"] = 1 });
        // Act
        await using var result = (await session.ExecuteAsync(request, CancellationToken.None))
            .ShouldBeAssignableTo<Assimalign.Cohesion.Database.Execution.DatabaseResultSet>();
        // Assert
        await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { row.GetValue(0).ShouldBe(expected); }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - JSON binding: common source alternatives retain metadata and raw JSONB (#1722)")]
    public async Task Execute_CommonSources_ShouldRetainSourceIdentityAsync()
    {
        // Arrange
        await using var engine = SqlPredicateExecutionTests.CreateEngine();
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        const string raw = "{ \"a\":1, \"a\":2 }";
        // Act / Assert
        foreach (string type in new[] { "JSON", "JSONB" })
        {
            foreach (string source in new[] { $"CASE WHEN FALSE THEN '{{}}' ELSE '{{\"k\":{raw}}}'::{type} END",
                $"COALESCE('{{\"k\":{raw}}}',NULL::{type})" })
            {
                await using var result = (await session.ExecuteAsync($"SELECT ({source})->'k'", cancellationToken: CancellationToken.None))
                    .ShouldBeAssignableTo<Assimalign.Cohesion.Database.Execution.DatabaseResultSet>();
                result.Columns[0].Type.ShouldBe(type == "JSON" ? DatabaseType.Json : DatabaseType.JsonBinary);
                await foreach (var row in result.GetRowsAsync(CancellationToken.None))
                {
                    object? actual = row.GetValue(0);
                    if (type == "JSONB") { actual = Encoding.UTF8.GetString(actual.ShouldBeOfType<byte[]>()); }
                    actual.ShouldBe(raw);
                }
            }
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - JSON binding: incompatible declared alternatives refuse on empty input (#1722)")]
    [InlineData("doc ->> CASE WHEN TRUE THEN 1 ELSE '1'::TEXT END")]
    [InlineData("doc ->> COALESCE(NULL::INT,'1'::TEXT)")]
    [InlineData("doc ->> CASE WHEN TRUE THEN 1 ELSE @text END")]
    [InlineData("(CASE WHEN TRUE THEN doc ELSE doc::JSONB END)->>'k'")]
    [InlineData("COALESCE(doc,doc::JSONB) IS JSON")]
    [InlineData("(CASE WHEN TRUE THEN doc ELSE '{}'::TEXT END) IS JSON")]
    public async Task Execute_DeclaredMismatch_ShouldRefuseBeforeRowsAsync(string expression)
    {
        // Arrange
        await using var engine = SqlPredicateExecutionTests.CreateEngine();
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SqlPredicateExecutionTests.ExecuteAsync(session, "CREATE TABLE empty_rows(doc JSON)");
        var request = SqlQueryRequest.FromSql("SELECT " + expression + " FROM empty_rows", new Dictionary<string, object?> { ["text"] = "1" });
        // Act / Assert
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(request, CancellationToken.None));
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS02080");
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - JSON binding: skipped unknown literals still undergo input conversion (#1722)")]
    public void Bind_InvalidUnknownAlternative_ShouldRefuseDuringAnalysis()
    {
        // Arrange
        var evaluator = new SqlExpressionEvaluator([], null);
        // Act / Assert
        foreach (string expression in new[] { "COALESCE(NULL::JSON,'bad')->'a'", "'[]'::JSON->CASE WHEN TRUE THEN 1 ELSE 'bad' END",
            "'[]'::JSON->NULLIF(CASE WHEN TRUE THEN 1 ELSE 'bad' END,0)" })
        {
            Should.Throw<DatabaseException>(() => evaluator.Bind(Expression(expression)))
                .Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS02022");
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - JSON chains: missing steps and failures retain selector evaluation order (#1722)")]
    [InlineData("'{\"a\":null}'::JSON->'a'->>'k'", null)]
    [InlineData("'{\"a\":{\"b\":1}}'::JSON->>'a'->>'b'", "CS01067")]
    [InlineData("'{}'::JSON->'missing'->>(1/0)::INT", "CS02001")]
    public void Evaluate_ChainedFailures_ShouldPreserveStepSemantics(string expression, string? code)
    {
        // Arrange
        var evaluator = new SqlExpressionEvaluator([], null);
        // Act / Assert
        if (code is null) { evaluator.Evaluate(Expression(expression), []).ShouldBeNull(); }
        else { Should.Throw<DatabaseException>(() => evaluator.Evaluate(Expression(expression), [])).Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(code); }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - JSON implication: identical partial predicates seek across restart (#1722)")]
    [InlineData("doc->>'k'='x'", "->>")]
    [InlineData("doc IS JSON OBJECT", "IS JSON OBJECT")]
    public async Task Execute_IdenticalPredicate_ShouldSeekBeforeAndAfterRestartAsync(string predicate, string proof)
    {
        // Arrange
        var storage = new CrashCaptureSqlStorageStrategy();
        var observed = new List<(string Path, string Proof)>();
        await using (var engine = SqlDatabaseEngine.Create("json-proof", new() { StorageStrategy = storage }))
        {
            var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
            await using var session = await database.CreateSessionAsync(CancellationToken.None);
            await SqlPredicateExecutionTests.ExecuteAsync(session, "CREATE TABLE t(id INT,doc JSON)");
            await SqlPredicateExecutionTests.ExecuteAsync(session, "INSERT INTO t VALUES(1,'{\"k\":\"x\"}')");
            await SqlPredicateExecutionTests.ExecuteAsync(session, "CREATE INDEX ix ON t(id) WHERE " + predicate);
            await Check(session);
        }
        // Act / Assert
        await using var reopened = SqlDatabaseEngine.Create("json-proof", new() { StorageStrategy = storage.CaptureDurableImages() });
        var restored = await reopened.OpenDatabaseAsync("db", CancellationToken.None);
        await using var reader = await restored.CreateSessionAsync(CancellationToken.None);
        await Check(reader);
        observed.Count.ShouldBe(2);
        foreach (var actual in observed)
        {
            actual.Path.ShouldBe("seek:ix");
            actual.Proof.ShouldContain("implied_by=", Case.Sensitive);
            actual.Proof.ShouldContain(proof, Case.Sensitive);
        }

        async Task Check(SqlDatabaseSession session)
        {
            (await SqlPredicateExecutionTests.RowsAsync(session, "SELECT id FROM t WHERE id=1 AND " + predicate))
                .ShouldBe(new object?[][] { [1] });
            string path = session.LastStatementMetrics!.AccessPath;
            var plan = await SqlPredicateExecutionTests.RowsAsync(session, "EXPLAIN SELECT id FROM t WHERE id=1 AND " + predicate);
            string rendered = string.Join(" ", plan.SelectMany(row => row).Select(value => value?.ToString()));
            observed.Add((path, rendered));
            _output.WriteLine($"{predicate}: {(observed.Count == 1 ? "before" : "after")} restart {path}");
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - JSON implication: structural identity maps offsets and refuses near misses (#1722)")]
    [InlineData("doc->>'k'='x'", "doc->>'other'='x'")]
    [InlineData("doc->'k'='1'::JSON", "doc->>'k'='1'")]
    [InlineData("doc->>'1'='x'", "doc->>1='x'")]
    [InlineData("doc IS JSON OBJECT", "doc IS JSON ARRAY")]
    [InlineData("doc IS JSON OBJECT", "doc IS NOT JSON OBJECT")]
    public void Prove_NearMisses_ShouldRequireStructuralIdentity(string predicate, string nearMiss)
    {
        // Arrange
        SqlCatalogColumn[] columns = [new("doc", new(DatabaseType.Json))];
        var scope = new SqlExpressionEvaluator(columns, null, foldConstants: false);
        var shifted = new SqlExpressionEvaluator([new("padding", new(DatabaseType.Int32)), .. columns], null, foldConstants: false);
        var bound = scope.BindPredicate(Expression(predicate));
        var witness = shifted.BindPredicate(Expression(predicate));
        // Act / Assert
        SqlPredicateImplication.Prove(bound, [witness], offset: 1).ShouldNotBeNull();
        SqlPredicateImplication.ReferencesOnly(witness, 1, 1, out bool found).ShouldBeTrue();
        found.ShouldBeTrue();
        SqlPredicateImplication.ReferencesOnly(witness, 0, 1).ShouldBeFalse();
        SqlPredicateImplication.Prove(bound, [scope.BindPredicate(Expression(nearMiss))]).ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - JSON chains: 32 steps allocate at most 1.5 times one step (#1722)")]
    public void Evaluate_LongChains_ShouldValidateAndSerializeOnce()
    {
        // Arrange: each extraction returns the same 64 KiB payload, at depths 1, 8, 32.
        var evaluator = new SqlExpressionEvaluator([new SqlCatalogColumn("doc", new(DatabaseType.Json))], null);
        int[] depths = [1, 8, 32];
        var expressions = depths.Select(depth => evaluator.Bind(Expression("doc" + string.Concat(Enumerable.Repeat("->'a'", depth))))).ToArray();
        var rows = depths.Select(depth => new object?[] { string.Concat(Enumerable.Repeat("{\"a\":", depth)) + "\"" + new string('x', 65_536) + "\"" + new string('}', depth) }).ToArray();
        for (int i = 0; i < depths.Length; i++) { Measure(i); }
        // Act
        long[] minima = [long.MaxValue, long.MaxValue, long.MaxValue];
        for (int round = 0; round < 3; round++)
        { for (int i = 0; i < depths.Length; i++) { minima[i] = Math.Min(minima[i], Measure(i)); } }
        _output.WriteLine($"64KiB chain bytes/op, minimum of 3 rounds: 1={minima[0]}, 8={minima[1]}, 32={minima[2]}");
        // Assert
        minima[0].ShouldBeGreaterThan(0);
        minima[1].ShouldBeLessThanOrEqualTo(minima[0] * 3 / 2);
        minima[2].ShouldBeLessThanOrEqualTo(minima[0] * 3 / 2);

        long Measure(int index)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            _ = evaluator.Evaluate(expressions[index], rows[index]);
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }
    }

    private static SqlExpression Expression(string text)
        => new SqlQueryParser().Parse("SELECT " + text).ShouldBeOfType<SqlQueryStatement>()
            .SqlExpression.ShouldBeOfType<SqlSelectExpression>().Columns[0].Expression;
}
