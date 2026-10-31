using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed class SqlJsonPathTests
{
    private readonly ITestOutputHelper _output;
    public SqlJsonPathTests(ITestOutputHelper output) { _output = output; }

    [Theory(DisplayName = "Cohesion Test [Sql] - SQL/JSON: PostgreSQL result and behavior corpus (#1722)")]
    [InlineData("JSON_VALUE('{\"a\":1}', '$.a')", "1")]
    [InlineData("JSON_VALUE('true', '$')", "t")]
    [InlineData("JSON_VALUE('false', '$')", "f")]
    [InlineData("JSON_VALUE('[1,2]', 'STRICT $[LAST - 1]')", "1")]
    [InlineData("JSON_QUERY('[1,2]', 'LaX $[0 TO LAST]' RETURNING TEXT WITH WRAPPER)", "[1, 2]")]
    [InlineData("JSON_VALUE('null', '$' DEFAULT 'empty' ON EMPTY)", null)]
    [InlineData("JSON_VALUE('null', '$' RETURNING JSON)", null)]
    [InlineData("JSON_VALUE('\"x\"', '$' RETURNING JSON)", "\"x\"")]
    [InlineData("JSON_VALUE('false', '$' RETURNING BOOLEAN)", false)]
    [InlineData("JSON_VALUE('\"yes\"', '$' RETURNING BOOLEAN)", true)]
    [InlineData("JSON_VALUE('\"o\"', '$' RETURNING BOOLEAN DEFAULT TRUE ON ERROR)", true)]
    [InlineData("JSON_VALUE('\"\u00a0true\"', '$' RETURNING BOOLEAN)", null)]
    [InlineData("JSON_QUERY('1', '$' RETURNING BOOLEAN)", true)]
    [InlineData("JSON_VALUE('{}', '$.a' RETURNING BOOLEAN DEFAULT 'of' ON EMPTY)", false)]
    [InlineData("JSON_VALUE('{}', '$.a' DEFAULT 'empty' ON EMPTY)", "empty")]
    [InlineData("JSON_VALUE('{}', '$.a' NULL ON EMPTY DEFAULT 'error' ON ERROR)", null)]
    [InlineData("JSON_VALUE('{}', 'strict $.a' NULL ON ERROR)", null)]
    [InlineData("JSON_VALUE('{}', '$.a' DEFAULT TRUE ON EMPTY)", "true")]
    [InlineData("JSON_VALUE('{}', '$.a' RETURNING INT DEFAULT TRUE ON EMPTY)", 1)]
    [InlineData("JSON_VALUE('{}', '$.a' DEFAULT '\"abc\"'::JSONB ON EMPTY)", "abc")]
    [InlineData("JSON_QUERY('{}', '$.a' RETURNING TEXT DEFAULT '{\"b\":1,\"a\":2}'::JSONB ON EMPTY)", "{\"a\": 2, \"b\": 1}")]
    [InlineData("JSON_VALUE('{}', 'strict $.a' DEFAULT 'empty' ON EMPTY DEFAULT 'error' ON ERROR)", "error")]
    [InlineData("JSON_VALUE('{}', '$' DEFAULT 'object' ON ERROR)", "object")]
    [InlineData("JSON_VALUE('[1,2]', '$[*]' DEFAULT 'many' ON ERROR)", "many")]
    [InlineData("JSON_VALUE('{\"a\":\"123\"}', '$.a' RETURNING INT)", 123)]
    [InlineData("JSON_VALUE('12.5', '$' RETURNING INT DEFAULT 7 ON ERROR)", 7)]
    [InlineData("JSON_VALUE('12.0', '$' RETURNING INT DEFAULT 7 ON ERROR)", 7)]
    [InlineData("JSON_VALUE('\"1e2\"', '$' RETURNING INT DEFAULT 8 ON ERROR)", 8)]
    [InlineData("JSON_VALUE('1e2', '$' RETURNING INT)", 100)]
    [InlineData("JSON_VALUE('1e100', '$' RETURNING INT DEFAULT 8 ON ERROR)", 8)]
    [InlineData("JSON_VALUE('\"bad\"', '$' RETURNING INT DEFAULT 9 ON ERROR)", 9)]
    [InlineData("JSON_VALUE('1.25e2', '$' RETURNING NUMERIC(5,1))", "125.0")]
    [InlineData("JSON_VALUE('\"long\"', '$' RETURNING VARCHAR(2) DEFAULT 'x' ON ERROR)", "x")]
    [InlineData("JSON_QUERY('[1,2]', '$[-4 to 0]' RETURNING TEXT WITH WRAPPER)", "[1]")]
    [InlineData("JSON_QUERY('[1,2]', '$[1 to 0]' RETURNING TEXT WITH WRAPPER EMPTY ARRAY ON EMPTY)", "[]")]
    [InlineData("JSON_QUERY('[1,2]', 'strict $[-1]' RETURNING TEXT DEFAULT 'range' ON ERROR)", "range")]
    [InlineData("JSON_VALUE('{\"é\":\"café\\uD83D\\uDE00\"}', '$.\"é\"')", "café😀")]
    [InlineData("JSON_QUERY('{}', '$.a' RETURNING TEXT EMPTY ARRAY ON EMPTY)", "[]")]
    [InlineData("JSON_QUERY('{}', '$.a' RETURNING TEXT DEFAULT 'empty' ON EMPTY)", "empty")]
    [InlineData("JSON_QUERY('{}', 'strict $.a' RETURNING TEXT EMPTY ARRAY ON ERROR)", "[]")]
    [InlineData("JSON_QUERY('{}', 'strict $.a' RETURNING TEXT NULL ON ERROR)", null)]
    [InlineData("JSON_QUERY('{}', 'strict $.a' RETURNING TEXT DEFAULT 'error' ON ERROR)", "error")]
    [InlineData("JSON_QUERY('{}', 'strict $.a' RETURNING TEXT EMPTY OBJECT ON ERROR)", "{}")]
    [InlineData("JSON_QUERY('[1,2]', '$[*]' RETURNING TEXT)", null)]
    [InlineData("JSON_QUERY('[1,2]', '$[*]' RETURNING TEXT WITH WRAPPER)", "[1, 2]")]
    [InlineData("JSON_QUERY('[1]', '$[*]' RETURNING TEXT WITH CONDITIONAL WRAPPER)", "1")]
    [InlineData("JSON_QUERY('1', '$' RETURNING TEXT WITH UNCONDITIONAL WRAPPER)", "[1]")]
    [InlineData("JSON_QUERY('true', '$' RETURNING BOOLEAN)", true)]
    [InlineData("JSON_QUERY('[]', '$[*]' RETURNING TEXT WITH WRAPPER EMPTY OBJECT ON EMPTY)", "{}")]
    [InlineData("JSON_QUERY('\"x\"', '$' RETURNING TEXT)", "\"x\"")]
    [InlineData("JSON_QUERY('\"x\"', '$' RETURNING TEXT OMIT QUOTES)", "x")]
    [InlineData("JSON_QUERY('\"x\"', '$' OMIT QUOTES)", null)]
    [InlineData("JSON_QUERY('\"12\"', '$' RETURNING INT OMIT QUOTES)", 12)]
    [InlineData("JSON_QUERY('\"12\"', '$' RETURNING INT)", null)]
    [InlineData("JSON_QUERY('null', '$' RETURNING TEXT)", "null")]
    [InlineData("JSON_QUERY('null', '$' RETURNING TEXT OMIT QUOTES)", "null")]
    [InlineData("JSON_QUERY('\"\u2028\"', '$' RETURNING TEXT)", "\"\u2028\"")]
    [InlineData("JSON_QUERY('{\"b\":1,\"a\":2,\"b\":3}', '$.*' RETURNING TEXT WITH WRAPPER)", "[2, 3]")]
    [InlineData("JSON_VALUE('{\"a\":1}', '$.\"\\u0061\"')", "1")]
    [InlineData("JSON_EXISTS('null', '$')", true)]
    [InlineData("JSON_EXISTS('{}', '$.a')", false)]
    [InlineData("JSON_EXISTS('{}', 'strict $.a')", false)]
    [InlineData("JSON_EXISTS('{}', 'strict $.a' FALSE ON ERROR)", false)]
    [InlineData("JSON_EXISTS('{}', 'strict $.a' TRUE ON ERROR)", true)]
    [InlineData("JSON_EXISTS('{}', 'strict $.a' UNKNOWN ON ERROR)", null)]
    [InlineData("JSON_EXISTS('[{\"a\":1},{}]', 'strict $[*].a' UNKNOWN ON ERROR)", null)]
    [InlineData("JSON_VALUE(NULL, '$' DEFAULT 'empty' ON EMPTY)", null)]
    [InlineData("JSON_QUERY(NULL, '$' EMPTY ARRAY ON EMPTY)", null)]
    [InlineData("JSON_EXISTS(NULL, '$' TRUE ON ERROR)", null)]
    [InlineData("JSON_VALUE('{\"a\":3}', @path)", null)]
    public void Evaluate_ResultCorpus_ShouldMatchPostgres(string source, object? expected)
    {
        // Arrange: PostgreSQL parse_expr.c:4639-4747, execExprInterp.c:4909-5304,
        // jsonpath_exec.c:4214-4395. Decimal parameters cannot be attribute values.
        if (source.Contains("NUMERIC", StringComparison.Ordinal)) { expected = 125.0m; }
        var evaluator = new SqlExpressionEvaluator([], new Dictionary<string, object?> { ["path"] = null });
        // Act
        object? actual = evaluator.Evaluate(Expression(source), []);
        // Assert
        actual.ShouldBe(expected);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - SQL/JSON: error boundaries preserve their coded condition (#1722)")]
    [InlineData("JSON_VALUE('broken', '$' DEFAULT 'caught' ON ERROR)", "CS02078")]
    [InlineData("JSON_VALUE('broken', @path)", "CS02078")]
    [InlineData("JSON_VALUE('0e1073741824', '$' NULL ON ERROR)", "CS02084")]
    [InlineData("JSON_VALUE('{\"a\":[1e999999],\"a\":1}', '$.a' NULL ON ERROR)", "CS02084")]
    [InlineData("JSON_VALUE('1', '$' RETURNING INT DEFAULT 'bad' ON ERROR)", "CS02022")]
    [InlineData("JSON_VALUE('{}', '$.a' ERROR ON EMPTY DEFAULT 'caught' ON ERROR)", "CS02083")]
    [InlineData("JSON_VALUE('{}', 'strict $.a' ERROR ON ERROR)", "CS02082")]
    [InlineData("JSON_VALUE('[1,2]', '$[*]' ERROR ON ERROR)", "CS05093")]
    [InlineData("JSON_QUERY('[1,2]', '$[*]' ERROR ON ERROR)", "CS05093")]
    [InlineData("JSON_QUERY('{}', '$.a' ERROR ON EMPTY DEFAULT '{}' ON ERROR)", "CS02083")]
    [InlineData("JSON_VALUE('{}', '$' ERROR ON ERROR)", "CS05093")]
    [InlineData("JSON_EXISTS('{}', 'strict $.a' ERROR ON ERROR)", "CS02082")]
    [InlineData("JSON_VALUE('{}', '$.a' RETURNING INT DEFAULT (1/0) ON EMPTY DEFAULT 5 ON ERROR)", "CS02001")]
    [InlineData("JSON_QUERY('{}', '$.a' RETURNING INT EMPTY ARRAY ON EMPTY)", "CS02022")]
    public void Evaluate_ErrorCorpus_ShouldPreserveBoundary(string source, string code)
    {
        // Arrange
        var evaluator = new SqlExpressionEvaluator([], new Dictionary<string, object?> { ["path"] = null });
        // Act / Assert
        Should.Throw<DatabaseException>(() => evaluator.Evaluate(Expression(source), [])).Diagnostics[0].Code.ShouldBe(code);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - SQL/JSON: generated accessor oracle covers lax and strict modes (#1722)")]
    public void Evaluate_GeneratedAccessors_ShouldMatchReferenceOracle()
    {
        // Arrange: expectations are constructed independently of the JSON parser/path
        // evaluator. References: jsonpath_exec.c:885-1005,1056-1100,4214-4395.
        int comparisons = 0;
        for (int seed = 0; seed < 32; seed++)
        {
            string document = $"{{\"a\":[{seed},{seed + 1},null],\"quoted name\":{seed + 2},\"obj\":{{\"b\":{seed + 3},\"a\":{seed + 4}}}}}";
            var corpus = new (string Path, string Expected)[]
            {
                ("$.a[0]", seed.ToString()), ("$.a[last]", "null"), ("$.a[last - 1]", (seed + 1).ToString()),
                ("$.a[0, 1]", $"{seed}, {seed + 1}"), ("$.a[0 to last]", $"{seed}, {seed + 1}, null"),
                ("$.a[*]", $"{seed}, {seed + 1}, null"), ("$.\"quoted name\"", (seed + 2).ToString()),
                ("$.obj.*", $"{seed + 4}, {seed + 3}"), ("$.obj.a", (seed + 4).ToString()),
            };
            var evaluator = new SqlExpressionEvaluator([], new Dictionary<string, object?> { ["doc"] = document });
            // Act / Assert
            foreach (string mode in new[] { "lax ", "strict " })
            {
            foreach (var test in corpus)
            {
                var actual = evaluator.Evaluate(Expression($"JSON_QUERY(@doc, '{mode}{test.Path}' RETURNING TEXT WITH WRAPPER ERROR ON ERROR)"), []);
                actual.ShouldBe("[" + test.Expected + "]"); comparisons++;
            }
            }
            foreach (var (input, path, expected) in new[]
            {
                ($"[{{\"a\":{seed}}},{{\"a\":{seed + 1}}}]", "$.a", $"[{seed}, {seed + 1}]"),
                ($"{seed}", "$[0]", $"[{seed}]"), ($"{seed}", "$[*]", $"[{seed}]"),
                ($"[[{{\"a\":{seed}}}]]", "$.a", "EMPTY"),
                ("{}", "$.missing", "EMPTY"), ("[]", "$[-5 to last]", "EMPTY"),
            })
            {
                var actual = new SqlExpressionEvaluator([], null).Evaluate(Expression($"JSON_QUERY('{input}', '{path}' RETURNING TEXT WITH WRAPPER DEFAULT 'EMPTY' ON EMPTY ERROR ON ERROR)"), []);
                actual.ShouldBe(expected); comparisons++;
            }
        }
        comparisons.ShouldBe(768);
        _output.WriteLine($"{comparisons} generated PostgreSQL accessor comparisons");
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - SQL/JSON: unsupported paths refuse before an empty source reads rows (#1722)")]
    [InlineData("$?(@.a == 1)")]
    [InlineData("$.type()")]
    [InlineData("$.size()")]
    [InlineData("$.double()")]
    [InlineData("$.keyvalue()")]
    [InlineData("$.datetime()")]
    [InlineData("$ + 1")]
    [InlineData("$ == 1")]
    [InlineData("$ && true")]
    [InlineData("$ like_regex \"x\"")]
    [InlineData("$ starts with \"x\"")]
    [InlineData("exists($)")]
    [InlineData("$ is unknown")]
    [InlineData("@")]
    [InlineData("$name")]
    [InlineData("$.a$b")]
    [InlineData("$.**")]
    [InlineData("$[last + 1]")]
    [InlineData("$[0,]")]
    public async Task Execute_UnsupportedPaths_ShouldRefuseBeforeRowsAsync(string path)
    {
        // Arrange
        await using var engine = SqlPredicateExecutionTests.CreateEngine();
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SqlPredicateExecutionTests.ExecuteAsync(session, "CREATE TABLE j(doc JSON)");
        // Act / Assert
        foreach (string expression in new[] { "JSON_VALUE(doc,@p)", "JSON_QUERY(doc,@p)", "JSON_EXISTS(doc,@p)", "CASE WHEN FALSE THEN JSON_VALUE(doc,@p) END" })
        {
            var request = SqlQueryRequest.FromSql("SELECT " + expression + " FROM j", new Dictionary<string, object?> { ["p"] = path });
            var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(request, CancellationToken.None));
            error.Diagnostics[0].Code.ShouldBe("CS02081");
        }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - SQL/JSON: binding handles context alternatives and parameters (#1722)")]
    public async Task Execute_ParametersAndAlternatives_ShouldBindBeforeRowsAsync()
    {
        // Arrange
        await using var engine = SqlPredicateExecutionTests.CreateEngine();
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SqlPredicateExecutionTests.ExecuteAsync(session, "CREATE TABLE j(doc JSON, p TEXT)");
        // Act / Assert
        foreach (string source in new[] { "JSON_VALUE(doc,p)", "JSON_VALUE(doc,'$'||'.a')", "JSON_VALUE(doc,'$' DEFAULT doc ON EMPTY)",
            "JSON_VALUE(doc,'$' DEFAULT (1 IN (SELECT 1)) ON EMPTY)", "JSON_VALUE(doc,'$' DEFAULT ('x' COLLATE case_insensitive) ON EMPTY)" })
        {
            var error = await Should.ThrowAsync<DatabaseException>(() => SqlPredicateExecutionTests.RowsAsync(session, "SELECT " + source + " FROM j"));
            error.Diagnostics[0].Code.ShouldBe(source.Contains("DEFAULT", StringComparison.Ordinal) ? "CS01069" : "CS01068");
        }
        foreach (string source in new[] { "JSON_VALUE(CASE WHEN TRUE THEN '{}'::JSON ELSE 1 END,'$')", "JSON_VALUE(COALESCE('{}'::JSON,'{}'::JSONB),'$')" })
        { (await Should.ThrowAsync<DatabaseException>(() => SqlPredicateExecutionTests.RowsAsync(session, "SELECT " + source + " FROM j"))).Diagnostics[0].Code.ShouldBe("CS02080"); }
        var evaluator = new SqlExpressionEvaluator([], new Dictionary<string, object?> { ["doc"] = "{\"a\":42}", ["p"] = "$.a" });
        evaluator.Evaluate(Expression("JSON_VALUE(CASE WHEN TRUE THEN @doc ELSE NULL END,@p RETURNING INT)"), []).ShouldBe(42);
        evaluator.Evaluate(Expression("JSON_VALUE(COALESCE(NULL,@doc),@p)"), []).ShouldBe("42");
        evaluator.Evaluate(Expression("JSON_QUERY('{\"b\":1,\"a\":2,\"b\":3}', '$')"), []).ShouldBeOfType<byte[]>().ShouldBe(Encoding.UTF8.GetBytes("{\"a\": 2, \"b\": 3}"));
        await SqlPredicateExecutionTests.ExecuteAsync(session, "CREATE SEQUENCE s");
        await SqlPredicateExecutionTests.ExecuteAsync(session, "INSERT INTO j VALUES('{\"a\":1}', '$.a')");
        (await SqlPredicateExecutionTests.RowsAsync(session, "SELECT JSON_VALUE(doc,'$.a' RETURNING BIGINT DEFAULT (CASE WHEN nextval('s')>0 THEN 9 END) ON EMPTY), (SELECT j.p) FROM j")).ShouldHaveSingleItem()[0].ShouldBe(1L);
        (await SqlPredicateExecutionTests.RowsAsync(session, "SELECT nextval('s')")).ShouldHaveSingleItem()[0].ShouldBe(1L);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - SQL/JSON: wildcard work is linear and strict EXISTS checks all items (#1722)")]
    public void Evaluate_WildcardSizes_ShouldChargeLinearWork()
    {
        // Arrange / Act / Assert: counter proof, no clock threshold.
        foreach (int size in new[] { 100, 1000, 10000 })
        {
            using var document = JsonDocument.Parse("[" + string.Join(',', Enumerable.Repeat("1", size)) + "]");
            using var work = new SqlJsonPathExecution();
            SqlJsonPath.Parse("$[*]").Evaluate(document.RootElement, work, exists: false).Count.ShouldBe(size);
            work.Visits.ShouldBe(size + 1);
            work.Reserved.ShouldBe(32L * size);
            _output.WriteLine($"wildcard {size}: {work.Visits} visits, {work.Reserved} retained result bytes");
            using var early = new SqlJsonPathExecution();
            SqlJsonPath.Parse("$[*]").Evaluate(document.RootElement, early, exists: true).Count.ShouldBe(1);
            early.Visits.ShouldBe(2);
            using var strict = new SqlJsonPathExecution();
            SqlJsonPath.Parse("strict $[*]").Evaluate(document.RootElement, strict, exists: true).Count.ShouldBe(1);
            strict.Visits.ShouldBe(size + 1);
            strict.Reserved.ShouldBe(32);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - SQL/JSON: grouping and window plans retain scalar operands (#1722)")]
    public async Task Execute_GroupAndWindow_ShouldPreserveOperandsAsync()
    {
        // Arrange
        await using var engine = SqlPredicateExecutionTests.CreateEngine();
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SqlPredicateExecutionTests.ExecuteAsync(session, "CREATE TABLE j(id INT,doc JSON)");
        await SqlPredicateExecutionTests.ExecuteAsync(session, "INSERT INTO j VALUES(1,'{\"a\":1}'),(2,'{\"a\":1}'),(3,'{\"a\":2}')");
        // Act
        var groups = await SqlPredicateExecutionTests.RowsAsync(session,
            "SELECT JSON_VALUE(doc,'$.a' RETURNING INT) AS a,COUNT(*) FROM j GROUP BY JSON_VALUE(doc,'$.a' RETURNING INT) ORDER BY a");
        var windows = await SqlPredicateExecutionTests.RowsAsync(session,
            "SELECT JSON_VALUE(CAST(SUM(id) OVER() AS TEXT),'$' RETURNING INT) FROM j");
        // Assert
        groups.Count.ShouldBe(2); groups[0][0].ShouldBe(1); groups[0][1].ShouldBe(2L);
        groups[1][0].ShouldBe(2); groups[1][1].ShouldBe(1L);
        windows.Count.ShouldBe(3); windows.ShouldAllBe(row => Equals(row[0], 6));
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - SQL/JSON: work and memory grants refuse and unwind (#1722)")]
    public void Evaluate_Budgets_ShouldRefuseAndUnwind()
    {
        // Arrange
        var evaluator = new SqlExpressionEvaluator([], null);
        using var work = new SqlJsonPathExecution(budget: 1024);
        work.Enter();
        // Act / Assert: ON ERROR cannot suppress a budget refusal.
        Should.Throw<SqlExecutionException>(() => evaluator.Evaluate(Expression("JSON_QUERY('[1,2,3,4,5,6,7,8,9,10]', '$[*]' WITH WRAPPER NULL ON ERROR)"), []))
            .Diagnostics[0].Code.ShouldBe("CS05094");
        work.Reserved.ShouldBe(0);
        for (int index = 0; index < 1_000_000; index++)
        {
            try { work.Visit(); }
            catch (SqlExecutionException error) { error.Diagnostics[0].Code.ShouldBe("CS05094"); break; }
        }
        Should.Throw<SqlExecutionException>(work.Visit).Diagnostics[0].Code.ShouldBe("CS05094");
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - SQL/JSON: parser limits and cancellation bound path work (#1722)")]
    public void Evaluate_PathLimitsAndCancellation_ShouldRefuse()
    {
        // Arrange
        string atLimit = "$" + string.Concat(Enumerable.Repeat("[0]", 256));
        using var document = JsonDocument.Parse("1");
        using var work = new SqlJsonPathExecution();
        // Act / Assert
        SqlJsonPath.Parse(atLimit).Evaluate(document.RootElement, work, false).ShouldHaveSingleItem().GetInt32().ShouldBe(1);
        Should.Throw<SqlExecutionException>(() => SqlJsonPath.Parse(atLimit + "[0]")).Diagnostics[0].Code.ShouldBe("CS05094");
        Should.Throw<SqlExecutionException>(() => SqlJsonPath.Parse("$" + new string(' ', 65536))).Diagnostics[0].Code.ShouldBe("CS05094");
        using var cancelled = new SqlJsonPathExecution(cancellationToken: new CancellationToken(true));
        cancelled.Enter();
        Should.Throw<OperationCanceledException>(() => new SqlExpressionEvaluator([], null)
            .Evaluate(Expression("JSON_EXISTS('1','$' TRUE ON ERROR)"), []));
        cancelled.Reserved.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - SQL/JSON: path visits share the existing window statement work budget (#1722)")]
    public void Evaluate_CombinedWindowWork_ShouldShareBudget()
    {
        // Arrange
        var metrics = new SqlStatementMetrics();
        using var work = SqlJsonPathExecution.ForStatement(metrics, 64 * 1024 * 1024, CancellationToken.None);
        // Act
        work.Visit();
        for (int visit = 1; visit < SqlStatementMetrics.StatementWorkVisitLimit; visit++) { metrics.RecordWindowFrameRecomputation(); }
        // Assert
        Should.Throw<SqlExecutionException>(work.Visit).Diagnostics[0].Code.ShouldBe("CS05094");
        Should.Throw<SqlExecutionException>(metrics.RecordWindowFrameRecomputation).Diagnostics[0].Code.ShouldBe("CS05094");
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - SQL/JSON: cached binary defaults detach every delivered value (#1722)")]
    public void Evaluate_BinaryDefaults_ShouldDetachValues()
    {
        // Arrange
        var evaluator = new SqlExpressionEvaluator([], null);
        var bound = evaluator.Bind(Expression("JSON_QUERY('{}', '$.missing' DEFAULT '[1]' ON EMPTY)"));
        // Act
        var first = evaluator.Evaluate(bound, []).ShouldBeOfType<byte[]>();
        first[0] = (byte)'x';
        var second = evaluator.Evaluate(bound, []).ShouldBeOfType<byte[]>();
        // Assert
        second.ShouldBe(Encoding.UTF8.GetBytes("[1]"));
        second.ShouldNotBeSameAs(first);
        evaluator.Evaluate(Expression("JSON_QUERY('{}', '$.missing' DEFAULT '\"abc\"'::JSONB ON EMPTY)"), [])
            .ShouldBeOfType<byte[]>().ShouldBe(Encoding.UTF8.GetBytes("\"abc\""));
        evaluator.Evaluate(Expression("JSON_QUERY('{}', '$.missing' DEFAULT '{\"b\":1,\"a\":2}'::TEXT ON EMPTY)"), [])
            .ShouldBeOfType<byte[]>().ShouldBe(Encoding.UTF8.GetBytes("{\"a\": 2, \"b\": 1}"));
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - SQL/JSON: nested statements retain independent ledgers (#1722)")]
    public void Evaluate_NestedStatementScopes_ShouldRestoreBudgets()
    {
        // Arrange
        var outerMetrics = new SqlStatementMetrics();
        var innerMetrics = new SqlStatementMetrics();
        using var outer = SqlJsonPathExecution.ForStatement(outerMetrics, 4096, CancellationToken.None);
        outer.Enter(); outer.Visit();
        // Act
        using (SqlJsonPathExecution.BeginPlanning(innerMetrics, 8192, CancellationToken.None))
        {
            var inner = SqlJsonPathExecution.ForExpression(CancellationToken.None);
            inner.ShouldNotBeSameAs(outer); inner.Visit();
            using (inner) { inner.Enter(); inner.Reserve(128); SqlJsonPathExecution.IsStatement(innerMetrics).ShouldBeTrue(); }
        }
        // Assert
        SqlJsonPathExecution.Current.ShouldBeSameAs(outer);
        SqlJsonPathExecution.IsStatement(outerMetrics).ShouldBeTrue();
        outer.Visits.ShouldBe(1); innerMetrics.HashMemoryBytes.ShouldBe(0);
        SqlJsonPathExecution.VisitsFor(innerMetrics).ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - SQL/JSON: statement memory and RETURNING domains share execution rules (#1722)")]
    public async Task Execute_StatementBudgetAndDomains_ShouldHandleOnlyResultErrorsAsync()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("json-budget", new() { StatementMemoryBudget = 32_768 });
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SqlPredicateExecutionTests.ExecuteAsync(session, "CREATE DOMAIN positive AS INT CHECK(VALUE > 0)");
        var request = SqlQueryRequest.FromSql("SELECT JSON_QUERY(@doc,'$[*]' WITH WRAPPER NULL ON ERROR)",
            new Dictionary<string, object?> { ["doc"] = "[" + string.Join(',', Enumerable.Repeat("1", 1000)) + "]" });
        // Act / Assert
        (await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(request, CancellationToken.None))).Diagnostics[0].Code.ShouldBe("CS05094");
        session.LastStatementMetrics.ShouldNotBeNull().HashMemoryBytes.ShouldBe(0);
        session.LastStatementMetrics.PeakHashMemoryBytes.ShouldBeGreaterThan(0);
        (await Should.ThrowAsync<DatabaseException>(() => SqlPredicateExecutionTests.RowsAsync(session,
            "SELECT JSON_VALUE('\"x\"','$' RETURNING CHAR(1000000) NULL ON ERROR)"))).Diagnostics[0].Code.ShouldBe("CS05094");
        (await SqlPredicateExecutionTests.RowsAsync(session, "SELECT JSON_VALUE('-1','$' RETURNING positive DEFAULT 5 ON ERROR)")).ShouldHaveSingleItem()[0].ShouldBe(5);
        (await SqlPredicateExecutionTests.RowsAsync(session, "SELECT JSON_VALUE('2','$' RETURNING positive)")).ShouldHaveSingleItem()[0].ShouldBe(2);
        (await SqlPredicateExecutionTests.RowsAsync(session, "SELECT JSON_VALUE('2','$' RETURNING positive DEFAULT '-1' ON EMPTY)")).ShouldHaveSingleItem()[0].ShouldBe(2);
        (await Should.ThrowAsync<DatabaseException>(() => SqlPredicateExecutionTests.RowsAsync(session,
            "SELECT JSON_VALUE('{}','$.a' RETURNING positive DEFAULT '-1' ON EMPTY DEFAULT 5 ON ERROR)"))).Diagnostics[0].Code.ShouldBe("CS03038");
        await SqlPredicateExecutionTests.ExecuteAsync(session, "CREATE DOMAIN nonnull AS INT CHECK(VALUE IS NOT NULL)");
        (await SqlPredicateExecutionTests.RowsAsync(session, "SELECT JSON_VALUE(NULL,'$' RETURNING nonnull DEFAULT 7 ON ERROR),JSON_QUERY(NULL,'$' RETURNING nonnull DEFAULT 8 ON ERROR)"))
            .ShouldHaveSingleItem().ShouldBe(new object?[] { 7, 8 });
        await using var nullPath = (DatabaseResultSet)await session.ExecuteAsync(SqlQueryRequest.FromSql(
            "SELECT JSON_VALUE('1',@path RETURNING nonnull DEFAULT 9 ON ERROR)", new Dictionary<string, object?> { ["path"] = null }), CancellationToken.None);
        int delivered = 0;
        await foreach (var row in nullPath.GetRowsAsync(CancellationToken.None)) { row.GetValue(0).ShouldBe(9); delivered++; }
        delivered.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - SQL/JSON: views, defaults, checks and partial predicates survive restart (#1722)")]
    public async Task Open_JsonPathDefinitions_ShouldRoundTripAsync()
    {
        // Arrange
        var storage = new CrashCaptureSqlStorageStrategy();
        await using (var engine = SqlDatabaseEngine.Create("json", new() { StorageStrategy = storage }))
        {
            var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
            await using var session = await database.CreateSessionAsync(CancellationToken.None);
            await SqlPredicateExecutionTests.ExecuteAsync(session, "CREATE TABLE j(id INT DEFAULT JSON_VALUE('{\"n\":2}', '$.n' RETURNING INT), doc JSON DEFAULT '{\"a\":3}', CHECK(JSON_EXISTS(doc,'$.a')))" );
            await SqlPredicateExecutionTests.ExecuteAsync(session, "CREATE VIEW jv AS SELECT JSON_VALUE(doc,'$.a' RETURNING INT) AS a, JSON_QUERY(doc,'$') AS doc, JSON_EXISTS(doc,'$.a') AS present FROM j");
            await SqlPredicateExecutionTests.ExecuteAsync(session, "CREATE INDEX jix ON j(id) WHERE JSON_VALUE(doc,'$.a' RETURNING INT)=3");
            await SqlPredicateExecutionTests.ExecuteAsync(session, "INSERT INTO j DEFAULT VALUES");
        }
        // Act
        await using var reopened = SqlDatabaseEngine.Create("json", new() { StorageStrategy = storage.CaptureDurableImages() });
        var restored = await reopened.OpenDatabaseAsync("db", CancellationToken.None);
        await using var reader = await restored.CreateSessionAsync(CancellationToken.None);
        await SqlPredicateExecutionTests.ExecuteAsync(reader, "INSERT INTO j DEFAULT VALUES");
        // Assert
        var rows = await SqlPredicateExecutionTests.RowsAsync(reader, "SELECT a,present FROM jv");
        rows.Count.ShouldBe(2);
        rows.ShouldAllBe(row => Equals(row[0], 3) && Equals(row[1], true));
        (await SqlPredicateExecutionTests.RowsAsync(reader, "SELECT id FROM j WHERE id=2 AND JSON_VALUE(doc,'$.a' RETURNING INT)=3")).Count.ShouldBe(2);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - SQL/JSON: format 32 is refused before persisted expressions are reinterpreted (#1722)")]
    public async Task Open_PreJsonPathFormat_ShouldRefuseAsync()
    {
        // Arrange
        var storage = new CrashCaptureSqlStorageStrategy();
        await using (var engine = SqlDatabaseEngine.Create("json-format", new() { StorageStrategy = storage }))
        {
            var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
            await database.Catalog.SetRecordSpaceFormatVersionAsync(32, CancellationToken.None);
        }
        await using var reopened = SqlDatabaseEngine.Create("json-format", new() { StorageStrategy = storage.CaptureDurableImages() });
        // Act / Assert
        var refusal = await Should.ThrowAsync<SqlDataStorageFormatException>(async () => await reopened.OpenDatabaseAsync("db", CancellationToken.None));
        refusal.Message.ShouldContain("supports only format 33", Case.Sensitive);
    }

    private static SqlExpression Expression(string source)
    {
        var statement = new SqlQueryParser().Parse("SELECT " + source + ";").ShouldBeOfType<SqlQueryStatement>();
        statement.Diagnostics.Select(diagnostic => diagnostic.Code + ": " + diagnostic.Message).ShouldBeEmpty();
        return statement.SqlExpression.ShouldBeOfType<SqlSelectExpression>().Columns[0].Expression;
    }
}
