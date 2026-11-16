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

public sealed class SqlCommonTypePass4Tests
{
    public static IEnumerable<object[]> TransparentSources()
    {
        foreach (bool tcp in new[] { false, true })
        {
            foreach (bool populated in new[] { false, true })
            {
                foreach (string sql in new[]
                {
                    "SELECT COALESCE(ABS(unproved_result(id,NULL)),0) FROM t",
                    "SELECT COALESCE(-unproved_result(id,NULL),0) FROM t",
                    "SELECT COALESCE(+unproved_result(id,NULL),0) FROM t",
                    "SELECT COALESCE(DATE_TRUNC('day',unproved_result(id,NULL)),TIMESTAMP '2001-01-01') FROM t",
                    "SELECT COALESCE(MAX(unproved_result(id,NULL)),0) FROM t",
                    "SELECT COALESCE(MIN(unproved_result(id,NULL)),0) FROM t",
                    "SELECT COALESCE(any_value(NULL) OVER (),0) FROM t",
                    "SELECT COALESCE(any_value(NULL),0) FROM t",
                    "SELECT COALESCE(any_value(NULL) OVER (),0) FROM t GROUP BY id",
                    "SELECT COALESCE(MAX(unproved_result(id,NULL)) OVER (),0) FROM t",
                    "SELECT CASE WHEN TRUE THEN 0 ELSE ABS(-unproved_result(id,NULL)) END FROM t",
                    "SELECT COALESCE(ABS(unproved_result(id,NULL)),0) FROM t GROUP BY ABS(unproved_result(id,NULL))",
                    "SELECT COALESCE(ABS(NULLIF(unproved_result(id,NULL),1)),0) FROM t",
                    "SELECT COALESCE(NULLIF(unproved_result(id,NULL),1),0) FROM t GROUP BY NULLIF(unproved_result(id,NULL),1)",
                }) { yield return [sql, populated, tcp]; }
            }
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - CASE/COALESCE: transparent wrappers retain application-source planning refusals (#1788)")]
    [MemberData(nameof(TransparentSources))]
    public Task Plan_TransparentApplicationSource_ShouldRefuseBeforeRows(string sql, bool populated, bool tcp)
        => RunAsync(tcp, async (session, connection, token) =>
        {
            // Arrange: test empty and populated input independently, including grouped window arguments.
            await session.ExecuteAsync("CREATE TABLE t(id INT)", cancellationToken: token);
            if (populated) { await session.ExecuteAsync("INSERT INTO t VALUES(1)", cancellationToken: token); }

            // Act: ExecuteAsync must refuse before a result set can be read.
            string code = await PlanningFailureAsync(session, connection, sql, null, token);

            // Assert
            code.ShouldBe("CS02080");
        });

    public static IEnumerable<object[]> UnsupportedParameters()
    {
        foreach (bool tcp in new[] { false, true })
        {
            foreach (bool populated in new[] { false, true })
            {
                foreach (string sql in new[]
                {
                    "SELECT COALESCE(ABS(@p),0)",
                    "SELECT COALESCE(ABS(@p),0) FROM t",
                    "SELECT COALESCE(MAX(@p),0) FROM t",
                    "SELECT COALESCE(DATE_TRUNC('day',@p),TIMESTAMP '2001-01-01')",
                    "SELECT COALESCE(DATE_TRUNC('day',@p),TIMESTAMP '2001-01-01') FROM t",
                }) { yield return [sql, populated, tcp]; }
            }
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - CASE/COALESCE: unsupported CLR parameters refuse before rows (#1788)")]
    [MemberData(nameof(UnsupportedParameters))]
    public Task Execute_UnsupportedParameterUnderWrapper_ShouldRefuseBeforeRows(string sql, bool populated, bool tcp)
        => RunAsync(tcp, async (session, connection, token) =>
        {
            // Arrange: an unsupported CLR object has no wire representation.
            await session.ExecuteAsync("CREATE TABLE t(id INT)", cancellationToken: token);
            if (populated) { await session.ExecuteAsync("INSERT INTO t VALUES(1)", cancellationToken: token); }
            var parameters = new Dictionary<string, object?> { ["p"] = new object() };

            // Act / Assert: in-process planning refuses; TCP encoding refuses before sending SQL.
            if (connection is null)
            {
                (await PlanningFailureAsync(session, connection, sql, parameters, token)).ShouldBe("CS02080");
            }
            else
            {
                await Should.ThrowAsync<DatabaseTypeException>(() => connection.QueryAsync(sql, parameters, token).AsTask());
            }
        });

    [Theory(DisplayName = "Cohesion Test [Sql] - CASE/COALESCE: NULL parameter values are untyped until CAST (#1788)")]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public Task Execute_PolymorphicParameter_ShouldUseValueTypeOrCast(bool cast, bool nonNull, bool tcp)
        => RunAsync(tcp, async (session, connection, token) =>
        {
            // Arrange
            string sql = cast ? "SELECT COALESCE(pick(CAST(@p AS BIGINT),1),0)" : "SELECT COALESCE(pick(@p,1),0)";
            var parameters = new Dictionary<string, object?> { ["p"] = nonNull ? 5 : null };

            // Act / Assert
            if (!cast && !nonNull)
            { (await PlanningFailureAsync(session, connection, sql, parameters, token)).ShouldBe("CS02080"); }
            else
            {
                var rows = await ReadAsync(session, connection, sql, parameters, token);
                rows.ShouldBe(new object?[][] { [cast ? (object)(nonNull ? 5L : 0L) : 5] });
            }
        });

    public static IEnumerable<object[]> NullControls()
    {
        foreach (bool tcp in new[] { false, true })
        {
            foreach (string expression in new[]
            {
                "ABS(NULL)", "ABS(NULLIF(NULL,1))", "ABS(@p)", "DATE_TRUNC('day',@p)",
                "-NULL", "+NULL", "MAX(NULL)", "MIN(NULL)", "MAX(NULL) OVER ()",
            }) { yield return [expression, tcp]; }
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - CASE/COALESCE: transparent NULL forms keep their values in process and over TCP (#1788)")]
    [MemberData(nameof(NullControls))]
    public Task Execute_TransparentNullControl_ShouldReturnValue(string expression, bool tcp)
        => RunAsync(tcp, async (session, connection, token) =>
        {
            // Arrange
            await session.ExecuteAsync("CREATE TABLE t(id INT)", cancellationToken: token);
            await session.ExecuteAsync("INSERT INTO t VALUES(1)", cancellationToken: token);
            var parameters = new Dictionary<string, object?> { ["p"] = null };
            string fallback = expression.StartsWith("DATE_TRUNC", StringComparison.Ordinal) ? "TIMESTAMP '2001-01-01'" : "2";

            // Act
            var value = await ReadAsync(session, connection, "SELECT " + expression + " FROM t", parameters, token);
            var coalesced = await ReadAsync(session, connection, $"SELECT COALESCE({expression},{fallback}) FROM t", parameters, token);

            // Assert
            value.ShouldBe(new object?[][] { [null] });
            object expected = fallback.StartsWith("TIMESTAMP", StringComparison.Ordinal) ? new DateTime(2001, 1, 1)
                : expression is "MAX(NULL)" or "MIN(NULL)" ? 2L : (object)2;
            coalesced.ShouldBe(new object?[][] { [expected] });
        });

    [Theory(DisplayName = "Cohesion Test [Sql] - CASE/COALESCE: query boundaries report Null-typed application values at a row (#1788)")]
    [InlineData("SELECT COALESCE((SELECT unproved_result(1,NULL)),0) FROM t", false)]
    [InlineData("SELECT COALESCE(v,0) FROM viewed", false)]
    [InlineData("WITH q AS (SELECT unproved_result(id,NULL) AS v FROM t) SELECT COALESCE(v,0) FROM q", false)]
    [InlineData("SELECT COALESCE((SELECT unproved_result(1,NULL)),0) FROM t", true)]
    [InlineData("SELECT COALESCE(v,0) FROM viewed", true)]
    [InlineData("WITH q AS (SELECT unproved_result(id,NULL) AS v FROM t) SELECT COALESCE(v,0) FROM q", true)]
    public Task Execute_QueryBoundaryWithApplicationValue_ShouldFailAtRow(string sql, bool tcp)
        => RunAsync(tcp, async (session, connection, token) =>
        {
            // Arrange: an empty result proves planning accepts each boundary shape.
            await session.ExecuteAsync("CREATE TABLE t(id INT)", cancellationToken: token);
            await session.ExecuteAsync("CREATE VIEW viewed AS SELECT unproved_result(id,NULL) AS v FROM t", cancellationToken: token);
            (await ReadAsync(session, connection, sql, null, token)).ShouldBeEmpty();
            await session.ExecuteAsync("INSERT INTO t VALUES(1)", cancellationToken: token);

            // Act / Assert: execution can materialize rows before returning the result set.
            // Acceptance on empty input distinguishes this row-time failure from planning.
            if (connection is null)
            {
                var failure = await Should.ThrowAsync<DatabaseException>(() => ReadAsync(session, connection, sql, null, token));
                failure.Diagnostics[0].Code.ShouldBe("CS02011");
            }
            else
            {
                var failure = await Should.ThrowAsync<SqlClientException>(() => ReadAsync(session, connection, sql, null, token));
                failure.Diagnostics[0].Code.ShouldBe("CS02011");
            }
        });

    public static IEnumerable<object[]> ComputedSources()
    {
        foreach (bool tcp in new[] { false, true })
        {
            foreach (string expression in new[]
            {
                "unproved_result(id,NULL) + 1", "ROUND(unproved_result(id,NULL))",
                "POWER(unproved_result(id,NULL),2)", "MOD(unproved_result(id,NULL),2)",
                "FLOOR(unproved_result(id,NULL))", "CEIL(unproved_result(id,NULL))",
                "CEILING(unproved_result(id,NULL))", "unproved_result(id,NULL) + unproved_result(id,NULL)",
            }) { yield return [expression, tcp]; }
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - CASE/COALESCE: computed application values plan but fail common-type conversion at id 1 (#1788)")]
    [MemberData(nameof(ComputedSources))]
    public Task Execute_ComputedApplicationValue_ShouldFailAtFirstRow(string expression, bool tcp)
        => RunAsync(tcp, async (session, connection, token) =>
        {
            // Arrange: empty input proves these computed nodes plan without refusing the inner call.
            await session.ExecuteAsync("CREATE TABLE t(id INT)", cancellationToken: token);
            string sql = $"SELECT COALESCE({expression},0) FROM t";
            (await ReadAsync(session, connection, sql, null, token)).ShouldBeEmpty();
            await session.ExecuteAsync("INSERT INTO t VALUES(1)", cancellationToken: token);

            // Act / Assert: the unresolved DOUBLE value cannot convert implicitly to the computed common type.
            if (connection is null)
            {
                var failure = await Should.ThrowAsync<DatabaseException>(() => ReadAsync(session, connection, sql, null, token));
                failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS02011");
            }
            else
            {
                var failure = await Should.ThrowAsync<SqlClientException>(() => ReadAsync(session, connection, sql, null, token));
                failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS02011");
            }
        });

    [Theory(DisplayName = "Cohesion Test [Sql] - CASE/COALESCE: decimal arithmetic over an unresolved application value returns 2.0 (#1788)")]
    [InlineData(false)]
    [InlineData(true)]
    public Task Execute_ComputedDecimalControl_ShouldReturnValue(bool tcp)
        => RunAsync(tcp, async (session, connection, token) =>
        {
            // Arrange
            await session.ExecuteAsync("CREATE TABLE t(id INT)", cancellationToken: token);
            const string sql = "SELECT COALESCE(unproved_result(id,NULL) + 1.0,0) FROM t";
            (await ReadAsync(session, connection, sql, null, token)).ShouldBeEmpty();
            await session.ExecuteAsync("INSERT INTO t VALUES(1)", cancellationToken: token);

            // Act
            var rows = await ReadAsync(session, connection, sql, null, token);

            // Assert
            rows.ShouldBe(new object?[][] { [2.0m] });
        });

    [Fact(DisplayName = "Cohesion Test [Sql] - CASE/COALESCE: Decimal ELSE identity conversion folds to a constant (#1788)")]
    public void Bind_DecimalCaseElse_ShouldFoldConstant()
    {
        // Arrange: inspect the bound tree, since an identity cast need not allocate even when evaluated.
        var syntax = ((SqlSelectExpression)SqlQueryRequest.FromSql("SELECT CASE WHEN flag THEN amount ELSE 0.0 END").Statement.SqlExpression)
            .Columns.Single().Expression;
        var evaluator = new SqlExpressionEvaluator([new SqlCatalogColumn("flag", new(DatabaseType.Boolean)),
            new SqlCatalogColumn("amount", new(DatabaseType.Decimal))], null);

        // Act
        var bound = evaluator.Bind(syntax).ShouldBeOfType<SqlBoundCase>();

        // Assert
        bound.ElseResult.ShouldBeOfType<SqlBoundConstant>().Value.ShouldBe(0m);
    }

    private static async Task RunAsync(bool tcp, Func<SqlDatabaseSession, SqlConnection?, CancellationToken, Task> action)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var builder = SqlDatabaseEngine.CreateBuilder("common-sources");
        builder.Functions.Add(new HashUnprovedResultFunction(true));
        builder.Functions.Add(new CommonTypeAnyValueFunction());
        builder.Functions.Add(new CommonTypePickFunction());
        await using var engine = await builder.BuildAsync(timeout.Token);
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        if (!tcp) { await action(session, null, timeout.Token); return; }
        await using var listener = TcpConnectionListener.Create(options => options.EndPoint = new IPEndPoint(IPAddress.Loopback, 0));
        await using var server = SqlDatabaseServer.Create(engine, new() { Listener = listener });
        await server.StartAsync(timeout.Token);
        await using var client = SqlClient.Create(new()
        {
            Settings = new DatabaseConnectionSettings { Database = "db", Principal = "common-types", EndPoint = listener.EndPoint },
            ConnectionFactory = new TcpConnectionFactory(),
        });
        await using var connection = await client.ConnectAsync(timeout.Token);
        await action(session, connection, timeout.Token);
    }

    private static async Task<string> PlanningFailureAsync(SqlDatabaseSession session, SqlConnection? connection, string sql,
        IReadOnlyDictionary<string, object?>? parameters, CancellationToken token)
    {
        if (connection is not null)
        {
            var failure = await Should.ThrowAsync<SqlClientException>(() => connection.QueryAsync(sql, parameters, token).AsTask());
            return failure.Diagnostics.ShouldHaveSingleItem().Code!;
        }
        var localFailure = await Should.ThrowAsync<DatabaseException>(async () =>
        { await using var rows = (DatabaseResultSet)await session.ExecuteAsync(sql, parameters, token); });
        return localFailure.Diagnostics.ShouldHaveSingleItem().Code!;
    }

    private static async Task<object?[][]> ReadAsync(SqlDatabaseSession session, SqlConnection? connection, string sql,
        IReadOnlyDictionary<string, object?>? parameters, CancellationToken token)
    {
        if (connection is not null)
        {
            var rows = await connection.QueryAsync(sql, parameters, token);
            return rows.Select(row => Enumerable.Range(0, row.FieldCount).Select(row.GetValue).ToArray()).ToArray();
        }
        await using var result = (DatabaseResultSet)await session.ExecuteAsync(sql, parameters, token);
        var values = new List<object?[]>();
        await foreach (var row in result.GetRowsAsync(token))
        { values.Add(Enumerable.Range(0, row.FieldCount).Select(row.GetValue).ToArray()); }
        return values.ToArray();
    }
}
