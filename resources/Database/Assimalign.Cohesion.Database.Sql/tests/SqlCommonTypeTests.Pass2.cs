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
using Assimalign.Cohesion.Database.Sql.Client;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlCommonTypeTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - CASE/COALESCE: windows and persisted window projections share scalar types (#1788)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Execute_WindowBoundaries_ShouldMatchPostgres17(bool tcp)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var builder = SqlDatabaseEngine.CreateBuilder("common-window");
        builder.Functions.Add(SqlScalarFunction.Create("kind", static (int value) => -value, SqlFunctionVolatility.Immutable));
        builder.Functions.Add(SqlScalarFunction.Create("kind", static (decimal value) => value, SqlFunctionVolatility.Immutable));
        await using var engine = await builder.BuildAsync(timeout.Token);
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
        await Execute(session, "CREATE TABLE w(id INT,c BOOLEAN,s SMALLINT,i INT,amount NUMERIC)");
        await Execute(session, "INSERT INTO w VALUES(1,FALSE,NULL,100000,2.5),(2,TRUE,2,7,3.5)");
        const string window = "SELECT CASE WHEN c THEN 1 ELSE 2.5 END AS v,ROW_NUMBER() OVER (ORDER BY id) AS r FROM w";

        // Act / Assert: PostgreSQL 17.11 values and output families.
        await Check(window + " ORDER BY id", [DatabaseType.Decimal, DatabaseType.Int64], [[2.5m, 1L], [1m, 2L]]);
        await Check("SELECT COALESCE(s,i),ROW_NUMBER() OVER (ORDER BY id) FROM w ORDER BY id",
            [DatabaseType.Int32, DatabaseType.Int64], [[100000, 1L], [2, 2L]]);
        await Check("SELECT CASE WHEN ROW_NUMBER() OVER (ORDER BY id)=1 THEN 0 ELSE amount END FROM w ORDER BY id",
            [DatabaseType.Decimal], [[0m], [3.5m]]);
        await Check("SELECT SUM(CASE WHEN c THEN 1 ELSE 2.5 END) OVER (ORDER BY id) FROM w ORDER BY id",
            [DatabaseType.Decimal], [[2.5m], [3.5m]]);
        await Check("SELECT MAX(CASE WHEN id=1 THEN 16777217 ELSE CAST(NULL AS REAL) END) OVER () FROM w GROUP BY id ORDER BY id",
            [DatabaseType.Float32], [[16777216f], [16777216f]]);
        await Check("SELECT SUM(CASE WHEN id=1 THEN 16777217 ELSE CAST(NULL AS REAL) END) OVER () FROM w GROUP BY id ORDER BY id",
            [DatabaseType.Decimal], [[16777220m], [16777220m]]);
        await Check("SELECT id,ROW_NUMBER() OVER () FROM w ORDER BY CASE WHEN c THEN 1 ELSE 2.5 END,id",
            [DatabaseType.Int32, DatabaseType.Int64], [[2, 2L], [1, 1L]]);
        await Check("SELECT id,ROW_NUMBER() OVER () AS r FROM w ORDER BY kind(CASE WHEN c THEN 1 ELSE 2.5 END),r",
            [DatabaseType.Int32, DatabaseType.Int64], [[2, 2L], [1, 1L]]);
        await Command("CREATE TABLE inserted(v NUMERIC,r BIGINT)");
        await Command("INSERT INTO inserted " + window);
        await Command("CREATE TABLE copied AS " + window);
        await Command("CREATE VIEW viewed AS " + window);
        foreach (string name in new[] { "inserted", "copied", "viewed" })
        { await Check("SELECT v,r FROM " + name + " ORDER BY r", [DatabaseType.Decimal, DatabaseType.Int64], [[2.5m, 1L], [1m, 2L]]); }

        async Task Command(string sql)
        {
            if (tcp) { await connection.ExecuteAsync(sql, cancellationToken: timeout.Token); }
            else { await Execute(session, sql); }
        }
        async Task Check(string sql, DatabaseType[] types, object?[][] expected)
        {
            var actual = await Pass2Observe(session, tcp ? connection : null, sql, timeout.Token);
            actual.Types.ShouldBe(types, sql);
            actual.Rows.ShouldBe(expected, sql);
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - CASE/COALESCE: grouping slots agree with DISTINCT and retain key values (#1788)")]
    [InlineData("COALESCE(d,r)", DatabaseType.Float32)]
    [InlineData("COALESCE(c4,'x')", DatabaseType.Character)]
    [InlineData("CASE WHEN c THEN 16777217 ELSE 16777216::REAL END", DatabaseType.Float32)]
    [InlineData("COALESCE(NULL,s,i,0)", DatabaseType.Int32)]
    public async Task Execute_RepeatedGroupingKeys_ShouldAgreeWithDistinct(string expression, DatabaseType type)
    {
        // Arrange
        await using var engine = SqlPredicateExecutionTests.CreateEngine();
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        await Execute(session, "CREATE TABLE t(c BOOLEAN,d NUMERIC(10,8),r REAL,c4 CHAR(4),s SMALLINT,i INT)");
        await Execute(session, "INSERT INTO t VALUES(TRUE,1.23456780,NULL,'a',NULL,100000),(FALSE,1.23456781,NULL,'a',NULL,100000)");
        var distinct = await Pass2Observe(session, null, $"SELECT DISTINCT {expression} FROM t ORDER BY 1");

        foreach (string grouping in new[] { expression, $"ROLLUP({expression})", $"CUBE({expression})", $"GROUPING SETS(({expression}),())" })
        {
            // Act: HAVING and ORDER BY repeat the key, including inside another expression.
            var actual = await Pass2Observe(session, null,
                $"SELECT {expression},COUNT(*) FROM t GROUP BY {grouping} HAVING {expression} IS NOT NULL ORDER BY {expression}");

            // Assert
            actual.Types.ShouldBe(new[] { type, DatabaseType.Int64 });
            actual.Rows.Select(row => row.Take(1).ToArray()).ToArray().ShouldBe(distinct.Rows);
            actual.Rows.ShouldHaveSingleItem()[1].ShouldBe(2L);
            if (type == DatabaseType.Character) { actual.Rows[0][0].ShouldBe("a   "); }
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - CASE/COALESCE: unresolved polymorphic and unsupported parameter alternatives refuse before rows (#1788)")]
    [InlineData("SELECT COALESCE(unproved_result(id,NULL),0) FROM t")]
    [InlineData("SELECT CASE WHEN TRUE THEN 0 ELSE unproved_result(id,NULL) END FROM t")]
    [InlineData("SELECT COALESCE(@p,0) FROM t")]
    [InlineData("SELECT CASE WHEN TRUE THEN 0 ELSE @p END FROM t")]
    [InlineData("SELECT COALESCE(@p,0) FROM t GROUP BY @p")]
    [InlineData("SELECT CASE WHEN TRUE THEN 0 ELSE @p END FROM t GROUP BY @p")]
    [InlineData("SELECT COALESCE(unproved_result(id,NULL),0) FROM t GROUP BY unproved_result(id,NULL)")]
    [InlineData("SELECT CASE WHEN TRUE THEN 0 ELSE unproved_result(id,NULL) END FROM t GROUP BY unproved_result(id,NULL)")]
    public async Task Plan_UnresolvedAlternative_ShouldRefuseBeforeRows(string sql)
    {
        // Arrange: an empty input prevents a row-time refusal from passing this test.
        var builder = SqlDatabaseEngine.CreateBuilder("unresolved-alternative");
        builder.Functions.Add(new HashUnprovedResultFunction(true, text: true));
        await using var engine = await builder.BuildAsync();
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        await Execute(session, "CREATE TABLE t(id INT)");

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () =>
            await session.ExecuteAsync(sql, new Dictionary<string, object?> { ["p"] = new object() }));

        // Assert
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS02080");
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - CASE/COALESCE: a NULL parameter stays unknown and skipped statistical literals bind (#1788)")]
    public async Task Plan_NullParameterAndStatisticalLiteral_ShouldKeepTheirContracts()
    {
        // Arrange
        await using var engine = SqlPredicateExecutionTests.CreateEngine();
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();

        // Act / Assert
        await using (var rows = (DatabaseResultSet)await session.ExecuteAsync("SELECT COALESCE(@p,2)", new Dictionary<string, object?> { ["p"] = null }))
        {
            rows.Columns[0].Type.ShouldBe(DatabaseType.Int32);
            await foreach (var row in rows.GetRowsAsync()) { row.GetValue(0).ShouldBe(2); }
        }
        var failure = await Should.ThrowAsync<DatabaseException>(() => SqlPredicateExecutionTests.RowsAsync(session, "SELECT VAR_POP(CAST(COALESCE(1,'x') AS REAL))"));
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS02022");
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - CASE/COALESCE: immutable bitwise alternatives remain valid index predicates at creation and reopen (#1788)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Open_PartialPredicateWithApplicationCast_ShouldBind(bool registerAtCreation)
    {
        // Arrange
        int calls = 0;
        var storage = new CrashCaptureSqlStorageStrategy();
        var builder = SqlDatabaseEngine.CreateBuilder("partial-common-original");
        builder.Options.StorageStrategy = storage;
        if (registerAtCreation) { Register(builder); }
        await using var original = await builder.BuildAsync();
        await using var writer = await (await original.CreateDatabaseAsync("db")).CreateSessionAsync();
        await Execute(writer, "CREATE TABLE t(k INT)");
        await Execute(writer, "INSERT INTO t VALUES(1),(2)");
        await Execute(writer, "CREATE INDEX ix ON t(k) WHERE COALESCE(k & 1,0)>0");
        var next = SqlDatabaseEngine.CreateBuilder("partial-common-open");
        next.Options.StorageStrategy = storage.CaptureDurableImages();
        Register(next);

        // Act
        await using var reopened = await next.BuildAsync();
        await using var session = await (await reopened.OpenDatabaseAsync("db")).CreateSessionAsync();
        await Execute(session, "INSERT INTO t VALUES(3)");

        // Assert
        (await SqlPredicateExecutionTests.RowsAsync(session, "SELECT k FROM t WHERE COALESCE(k & 1,0)>0 ORDER BY k"))
            .ShouldBe(new object?[][] { [1], [3] });
        calls.ShouldBe(0);

        void Register(SqlDatabaseEngineBuilder target)
        {
            target.Types.AddCast(SqlCast.Create<bool, int>(SqlType.Boolean, SqlType.Integer,
                _ => { calls++; return 42; }, SqlCastContext.Explicit));
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - CASE/COALESCE: declared alternatives exclude explicit and application casts (#1788)")]
    [InlineData(SqlCastContext.Explicit)]
    [InlineData(SqlCastContext.Implicit)]
    public async Task Execute_DeclaredAlternative_ShouldUseOnlyBuiltInImplicitCasts(SqlCastContext context)
    {
        // Arrange: even a registered implicit application cast is not a common-type conversion.
        int calls = 0;
        var builder = SqlDatabaseEngine.CreateBuilder("common-implicit-only");
        builder.Types.AddCast(SqlCast.Create<bool, int>(SqlType.Boolean, SqlType.Integer,
            _ => { calls++; return 42; }, context));
        builder.Functions.Add(new UnprovedBooleanResultFunction());
        await using var engine = await builder.BuildAsync();
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(() => SqlPredicateExecutionTests.RowsAsync(session,
            "SELECT COALESCE(unproved_boolean(CAST(NULL AS INT)),0)"));

        // Assert: CAST remains the explicit opt-in to the registered conversion.
        failure.Diagnostics[0].Code.ShouldBe("CS02011");
        calls.ShouldBe(0);
        (await SqlPredicateExecutionTests.RowsAsync(session, "SELECT CAST(unproved_boolean(CAST(NULL AS INT)) AS INT)"))
            .ShouldBe(new object?[][] { [42] });
        calls.ShouldBe(1);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - CASE/COALESCE: other Null-typed shapes retain their existing behavior (#1788)")]
    [InlineData("NULLIF(NULL,1)")]
    [InlineData("(SELECT NULL)")]
    [InlineData("-NULL")]
    [InlineData("+NULL")]
    [InlineData("NULL * TRUE")]
    [InlineData("v FROM (SELECT NULL AS v) q", true)]
    [InlineData("MAX(NULL)", false, DatabaseType.Int64)]
    [InlineData("MIN(NULL)", false, DatabaseType.Int64)]
    [InlineData("MAX(NULL) OVER ()")]
    public async Task Execute_OtherNullTypedShape_ShouldKeepItsValue(string expression, bool derived = false, DatabaseType type = DatabaseType.Int32)
    {
        // Arrange
        await using var engine = SqlPredicateExecutionTests.CreateEngine();
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        string sql = derived ? "SELECT COALESCE(v,2) FROM (SELECT NULL AS v) q" : $"SELECT COALESCE({expression},2)";

        // Act
        var actual = await Pass2Observe(session, null, sql);

        // Assert
        actual.Types.ShouldBe(new[] { type });
        object expected = type == DatabaseType.Int64 ? 2L : (object)2;
        actual.Rows.ShouldBe(new object?[][] { [expected] });
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - CASE/COALESCE: SUM over a REAL CASE keeps the existing REAL transition (#1788)")]
    public async Task Execute_RealSumCase_ShouldMatchDirectRealSum()
    {
        // Arrange: direct REAL SUM and AVG already lose these digits at 17e22bb08.
        await using var engine = SqlPredicateExecutionTests.CreateEngine();
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();

        // Act
        var actual = await Pass2Observe(session, null,
            "SELECT SUM(CASE WHEN TRUE THEN 16777217 ELSE CAST(NULL AS REAL) END),SUM(CAST(16777216 AS REAL)),AVG(CAST(16777216 AS REAL))");

        // Assert: PostgreSQL returns real 16777216; DIALECT records Decimal transitions.
        actual.Types.ShouldBe(new[] { DatabaseType.Decimal, DatabaseType.Decimal, DatabaseType.Decimal });
        actual.Rows.ShouldBe(new object?[][] { [16777220m, 16777220m, 16777220m] });
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Server harness: an injected engine rejects separate engine configuration (#1788)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Start_HarnessWithEngineAndConfiguration_ShouldRefuse(bool name)
    {
        // Arrange
        int calls = 0;
        await using var engine = SqlPredicateExecutionTests.CreateEngine();

        // Act
        var failure = await Should.ThrowAsync<ArgumentException>(() => name
            ? ServerTestHarness.StartAsync(engineName: "separate-name", engine: engine)
            : ServerTestHarness.StartAsync(configureEngine: _ => calls++, engine: engine));

        // Assert
        failure.ParamName.ShouldBe("engine");
        calls.ShouldBe(0);
    }

    private static async Task<(DatabaseType[] Types, object?[][] Rows)> Pass2Observe(SqlDatabaseSession session,
        SqlConnection? connection, string sql, CancellationToken cancellationToken = default)
    {
        if (connection is not null)
        {
            var result = await connection.QueryAsync(sql, cancellationToken: cancellationToken);
            return (result.Columns.Select(column => column.Type).ToArray(),
                result.Select(row => Enumerable.Range(0, row.FieldCount).Select(row.GetValue).ToArray()).ToArray());
        }
        await using var rows = (DatabaseResultSet)await session.ExecuteAsync(sql, cancellationToken: cancellationToken);
        var values = new List<object?[]>();
        await foreach (var row in rows.GetRowsAsync(cancellationToken))
        { values.Add(Enumerable.Range(0, row.FieldCount).Select(row.GetValue).ToArray()); }
        return (rows.Columns.Select(column => column.Type).ToArray(), values.ToArray());
    }
}
