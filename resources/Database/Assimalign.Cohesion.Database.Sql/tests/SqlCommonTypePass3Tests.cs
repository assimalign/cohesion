using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Connections.Tcp;
using Assimalign.Cohesion.Database.Client;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Client;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed class SqlCommonTypePass3Tests
{
    [Fact(DisplayName = "Cohesion Test [Sql] - CASE/COALESCE: the pseudo-returning built-in families use overload result rules (#1788)")]
    public void Bind_PseudoBuiltinFamilies_ShouldUseKnownResultRules()
    {
        // Arrange
        var functions = SqlFunctionCatalog.Standard.Where(function => function.ReturnType.IsPseudo
            && function.ResultRule is SqlResultRule.NumericElement or SqlResultRule.Builtin).ToArray();

        // Act / Assert: enumerate the entire family, including ROUND's second overload.
        functions.Select(function => function.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray()
            .ShouldBe(new[] { "ABS", "CEIL", "CEILING", "DATE_TRUNC", "FLOOR", "MOD", "POWER", "ROUND", "ROUND" });
        SqlFunctionCatalog.Standard.Where(function => function.ReturnType.IsPseudo && function.ResultRule == SqlResultRule.Declared)
            .Select(function => function.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray()
            .ShouldBe(new[] { "MAX", "MIN" });
        foreach (var function in functions)
        {
            var inputs = new DatabaseType[function.ParameterTypes.Length];
            var type = SqlFunctionResolver.ResultType(function, inputs);
            type.ShouldBe(function.Name is "ABS" or "DATE_TRUNC" ? DatabaseType.Null
                : function.Name == "POWER" ? DatabaseType.Float64 : DatabaseType.Decimal, function.Name);
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - CASE/COALESCE: restriction and index contexts bind identical alternatives (#1788)")]
    [InlineData(DatabaseType.Int32, "COALESCE(v,0)>0")]
    [InlineData(DatabaseType.Decimal, "COALESCE(v,0)>0")]
    [InlineData(DatabaseType.Int32, "CASE WHEN v IS NULL THEN 0 ELSE v END>0")]
    [InlineData(DatabaseType.Decimal, "CASE WHEN v IS NULL THEN 0 ELSE v END>0")]
    public void Bind_PartialRestrictionContexts_ShouldProveBothDirections(DatabaseType type, string predicate)
    {
        // Arrange: ON CONFLICT, SELECT and join restrictions all bind in Table context.
        var syntax = ((SqlSelectExpression)SqlQueryRequest.FromSql("SELECT v FROM t WHERE " + predicate).Statement.SqlExpression).Where!;
        var columns = new[] { new SqlCatalogColumn("v", new(type)) };
        var query = new SqlExpressionEvaluator(columns, null, foldConstants: false, bindingContext: SqlBindingContext.Table);
        var index = new SqlExpressionEvaluator(columns, null, foldConstants: false, bindingContext: SqlBindingContext.IndexPredicate);

        // Act
        var queryTree = query.BindPredicate(syntax);
        var indexTree = index.BindPredicate(syntax);

        // Assert
        SqlPredicateImplication.Prove(indexTree, [queryTree]).ShouldNotBeNull();
        SqlPredicateImplication.Prove(queryTree, [indexTree]).ShouldNotBeNull();
    }

    public static IEnumerable<object[]> NullBuiltinCases()
    {
        foreach (bool tcp in new[] { false, true })
        {
            foreach (string expression in new[]
            {
                "COALESCE(ABS(@p),0)", "CASE WHEN @p IS NULL THEN 0 ELSE ABS(@p) END",
                "COALESCE(ABS(NULL),0)", "COALESCE(ABS(NULLIF(NULL,1)),0)",
                "COALESCE(DATE_TRUNC('day',NULL),TIMESTAMP '2001-01-01')",
                "COALESCE(DATE_TRUNC('day',@p),TIMESTAMP '2001-01-01')",
                "COALESCE(ROUND(NULL),0)", "COALESCE(ROUND(NULL,1),0)", "COALESCE(FLOOR(NULL),0)",
                "COALESCE(CEIL(NULL),0)", "COALESCE(CEILING(NULL),0)", "COALESCE(POWER(NULL,1),0)",
                "COALESCE(MOD(NULL,1),0)", "DATE_TRUNC('day',NULL)",
            }) { yield return new object[] { expression, tcp }; }
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - CASE/COALESCE: NULL built-in results remain unknown in process and over TCP (#1788)")]
    [MemberData(nameof(NullBuiltinCases))]
    public async Task Execute_NullBuiltinAlternative_ShouldReturnFallback(string expression, bool tcp)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = SqlDatabaseEngine.Create("common-null-builtins", new());
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        await using var listener = TcpConnectionListener.Create(options => options.EndPoint = new IPEndPoint(IPAddress.Loopback, 0));
        await using var server = SqlDatabaseServer.Create(engine, new() { Listener = listener });
        await server.StartAsync(timeout.Token);
        await using var client = SqlClient.Create(new()
        {
            Settings = new DatabaseConnectionSettings { Database = "db", Principal = "common-types", EndPoint = listener.EndPoint },
            ConnectionFactory = new TcpConnectionFactory(),
        });
        await using var connection = await client.ConnectAsync(timeout.Token);
        var parameters = new Dictionary<string, object?> { ["p"] = null };

        // Act
        var actual = await Read(session, tcp ? connection : null, "SELECT " + expression, parameters, timeout.Token);

        // Assert
        object? expected = expression.StartsWith("DATE_TRUNC", StringComparison.Ordinal) ? null
            : expression.Contains("TIMESTAMP", StringComparison.Ordinal) ? new DateTime(2001, 1, 1)
            : expression.Contains("ROUND", StringComparison.Ordinal) || expression.Contains("FLOOR", StringComparison.Ordinal)
                || expression.Contains("CEIL", StringComparison.Ordinal) || expression.Contains("POWER", StringComparison.Ordinal)
                || expression.Contains("MOD", StringComparison.Ordinal) ? 0m : (object)0;
        actual.ShouldBe(new object?[][] { [expected] });
        if (expression.Contains("ABS(@p)", StringComparison.Ordinal))
        {
            parameters["p"] = 5;
            (await Read(session, tcp ? connection : null, "SELECT " + expression, parameters, timeout.Token))
                .ShouldBe(new object?[][] { [5L] });
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - CASE/COALESCE: partial-index inference matches restriction trees at creation and reopen (#1788)")]
    [InlineData("INT", "COALESCE(v,0)>0", true)]
    [InlineData("NUMERIC", "COALESCE(v,0)>0", true)]
    [InlineData("INT", "CASE WHEN v IS NULL THEN 0 ELSE v END>0", true)]
    [InlineData("NUMERIC", "CASE WHEN v IS NULL THEN 0 ELSE v END>0", true)]
    [InlineData("INT", "COALESCE(v,0)>0", false)]
    [InlineData("NUMERIC", "COALESCE(v,0)>0", false)]
    [InlineData("INT", "CASE WHEN v IS NULL THEN 0 ELSE v END>0", false)]
    [InlineData("NUMERIC", "CASE WHEN v IS NULL THEN 0 ELSE v END>0", false)]
    public async Task Open_AlternativePartialPredicate_ShouldKeepProof(string type, string predicate, bool conflict)
    {
        // Arrange
        var storage = new CrashCaptureSqlStorageStrategy();
        var builder = SqlDatabaseEngine.CreateBuilder("common-proof-original");
        builder.Options.StorageStrategy = storage;
        await using var original = await builder.BuildAsync(CancellationToken.None);
        await using var writer = await (await original.CreateDatabaseAsync("db")).CreateSessionAsync();
        await writer.ExecuteAsync($"CREATE TABLE t(k INT,v {type})");
        await writer.ExecuteAsync($"CREATE {(conflict ? "UNIQUE " : "")}INDEX ix ON t(k) WHERE {predicate}");

        // Act / Assert: a rebound predicate must have the same proof as a fresh one.
        await Check(writer, 1);
        var next = SqlDatabaseEngine.CreateBuilder("common-proof-reopened");
        next.Options.StorageStrategy = storage.CaptureDurableImages();
        await using var reopened = await next.BuildAsync(CancellationToken.None);
        await using var reader = await (await reopened.OpenDatabaseAsync("db")).CreateSessionAsync();
        await Check(reader, 2);

        async Task Check(SqlDatabaseSession session, int key)
        {
            if (conflict)
            {
                string sql = $"INSERT INTO t VALUES({key},1) ON CONFLICT(k) WHERE {predicate} DO NOTHING";
                await session.ExecuteAsync(sql);
                await session.ExecuteAsync(sql);
                (await Read(session, null, $"SELECT k FROM t WHERE k={key}"))
                    .ShouldBe(new object?[][] { [key] });
            }
            else
            {
                await session.ExecuteAsync($"INSERT INTO t VALUES({key},1)");
                string query = $"SELECT k FROM t WHERE {predicate} AND k={key}";
                string plan = string.Join('\n', (await Read(session, null, "EXPLAIN " + query)).Select(row => string.Join('|', row)));
                plan.ShouldContain("IndexSeek", Case.Sensitive);
                plan.ShouldContain("ix", Case.Sensitive);
                plan.ShouldContain("partial_predicate=", Case.Sensitive);
                plan.ShouldContain("implied_by=", Case.Sensitive);
                (await Read(session, null, query)).ShouldBe(new object?[][] { [key] });
            }
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - CASE/COALESCE: unreachable signed and unsigned literal failures stay deferred (#1788)")]
    [InlineData("SELECT CASE WHEN FALSE THEN -99999999999999999999 ELSE 1 END")]
    [InlineData("SELECT CASE WHEN FALSE THEN 99999999999999999999 ELSE 1 END")]
    [InlineData("SELECT COALESCE(1,-1e999)")]
    [InlineData("SELECT COALESCE(1,1e999)")]
    public async Task Execute_UnreachableLiteralFailure_ShouldReturnOne(string sql)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("common-deferred", new());
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();

        // Act
        var rows = await Read(session, null, sql);

        // Assert: integer and floating spellings retain their selected numeric family.
        Convert.ToDecimal(rows.ShouldHaveSingleItem().ShouldHaveSingleItem(), System.Globalization.CultureInfo.InvariantCulture).ShouldBe(1m);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - CASE/COALESCE: Null-typed derived columns and ordering aliases keep row-time behavior (#1788)")]
    [InlineData("SELECT COALESCE(v,0) FROM (SELECT id,unproved_result(id,NULL) AS v FROM t) q", "CS02011")]
    [InlineData("SELECT unproved_result(id,NULL) AS v FROM t ORDER BY COALESCE(v,0)", "CS02011")]
    public async Task Execute_NullTypedColumnWithValue_ShouldFailAtRow(string sql, string code)
    {
        // Arrange: empty input demonstrates acceptance before any runtime value exists.
        var builder = SqlDatabaseEngine.CreateBuilder("common-derived");
        builder.Functions.Add(new HashUnprovedResultFunction(true));
        await using var engine = await builder.BuildAsync();
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE t(id INT)");
        (await Read(session, null, sql)).ShouldBeEmpty();
        await session.ExecuteAsync("INSERT INTO t VALUES(1),(500)");

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(() => Read(session, null, sql));

        // Assert
        failure.Diagnostics[0].Code.ShouldBe(code);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - CASE/COALESCE: widening a folded parameter retains its provenance (#1788)")]
    public void Bind_FoldedParameterAlternative_ShouldKeepMarker()
    {
        // Arrange
        var syntax = ((SqlSelectExpression)SqlQueryRequest.FromSql("SELECT COALESCE(x,ABS(@p))").Statement.SqlExpression).Columns.Single().Expression;
        var evaluator = new SqlExpressionEvaluator([new SqlCatalogColumn("x", new(DatabaseType.Int64))],
            new Dictionary<string, object?> { ["p"] = 5 });

        // Act
        var bound = evaluator.Bind(syntax).ShouldBeOfType<SqlBoundCoalesce>();

        // Assert
        bound.Arguments[1].ShouldBeOfType<SqlBoundParameterConstant>().Value.ShouldBe(5L);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - CASE/COALESCE: an all-unknown grouping key keeps its scalar TEXT contract (#1788)")]
    public async Task Plan_UnknownGroupingKey_ShouldRefuseNumericAlternative()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("common-unknown-key", new());
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE t(id INT)");
        await session.ExecuteAsync("INSERT INTO t VALUES(1)");

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(() => Read(session, null,
            "SELECT COALESCE(COALESCE(NULL,NULL),1) FROM t GROUP BY COALESCE(NULL,NULL)"));

        // Assert
        failure.Diagnostics[0].Code.ShouldBe("CS02080");
    }

    private static async Task<object?[][]> Read(SqlDatabaseSession session, SqlConnection? connection, string sql,
        IReadOnlyDictionary<string, object?>? parameters = null, CancellationToken cancellationToken = default)
    {
        if (connection is not null)
        {
            var result = await connection.QueryAsync(sql, parameters, cancellationToken);
            return result.Select(row => Enumerable.Range(0, row.FieldCount).Select(row.GetValue).ToArray()).ToArray();
        }
        await using var rows = (DatabaseResultSet)await session.ExecuteAsync(sql, parameters, cancellationToken);
        var values = new List<object?[]>();
        await foreach (var row in rows.GetRowsAsync(cancellationToken))
        { values.Add(Enumerable.Range(0, row.FieldCount).Select(row.GetValue).ToArray()); }
        return values.ToArray();
    }
}
