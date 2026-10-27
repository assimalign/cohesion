using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;

namespace Assimalign.Cohesion.Database.Sql.Tests;

/// <summary>Exercises SQL definitions through the public SQL/session surface.</summary>
public sealed partial class SqlDefinedFunctionTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _output;

    /// <summary>Creates the fixture with diagnostic output for the Q8 gate.</summary>
    /// <param name="output">The test output sink.</param>
    public SqlDefinedFunctionTests(Xunit.Abstractions.ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - SQL definitions run in predicates, defaults, domains and other functions")]
    public async Task ExecuteAsync_SqlDefinitions_ShouldBindAndEnforceDependencies()
    {
        // Arrange
        await using var engine = await BuildAsync(_ => { });
        await using var session = await SessionAsync(engine);
        await ExecuteAsync(session, "CREATE FUNCTION plusone(p INT) RETURNS INT IMMUTABLE STRICT RETURN p + 1");
        await ExecuteAsync(session, "CREATE FUNCTION twice(p INT) RETURNS INT IMMUTABLE RETURN plusone(p) * 2");
        await ExecuteAsync(session, "CREATE DOMAIN positive AS INT DEFAULT 4 NOT NULL CONSTRAINT positive_check CHECK (VALUE > 0)");
        await ExecuteAsync(session, "CREATE TABLE items (id positive, n INT DEFAULT plusone(2), CHECK (plusone(n) > 0))");
        // Act
        await ExecuteAsync(session, "INSERT INTO items (id) VALUES (1)");
        var rows = await RowsAsync(session, "SELECT twice(n), plusone(NULL) FROM items WHERE plusone(id) = 2");
        // Assert
        rows.ShouldBe([[8, null]]);
        await ExecuteAsync(session, "INSERT INTO items (n) VALUES (2)");
        (await RowsAsync(session, "SELECT id FROM items WHERE n = 2")).ShouldBe([[4]]);
        (await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, "DROP FUNCTION plusone(INT)"))).Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS03302");
        (await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, "DROP DOMAIN positive"))).Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS03302");
        await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, "INSERT INTO items VALUES (-1, 2)"));
        await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, "INSERT INTO items VALUES (NULL, 2)"));
        await ExecuteAsync(session, "DROP TABLE items");
        await ExecuteAsync(session, "DROP FUNCTION twice(INT)");
        await ExecuteAsync(session, "DROP FUNCTION plusone(INT)");
        await ExecuteAsync(session, "DROP DOMAIN positive");
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - SQL functions default to volatile and called on NULL input")]
    public async Task ExecuteAsync_NullAndVolatility_ShouldFollowDeclaredContract()
    {
        // Arrange
        await using var engine = await BuildAsync(_ => { });
        await using var session = await SessionAsync(engine);
        await ExecuteAsync(session, "CREATE TABLE t (id INT)");
        await ExecuteAsync(session, "INSERT INTO t VALUES (1)");
        await ExecuteAsync(session, "CREATE FUNCTION fallback(p INT) RETURNS INT RETURN COALESCE(p, 9)");
        // Act
        var rows = await RowsAsync(session, "SELECT fallback(NULL) FROM t");
        // Assert
        rows.ShouldBe([[9]]);
        await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, "CREATE FUNCTION weak(p INT) RETURNS INT IMMUTABLE RETURN fallback(p)"));
        await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, "CREATE FUNCTION recursive(p INT) RETURNS INT RETURN recursive(p)"));
        await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, "CREATE TABLE invalid (id INT CHECK (fallback(id) > 0))"));
    }
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Native and SQL definitions share a namespace and native DROP is coded")]
    public async Task ExecuteAsync_NativeCollision_ShouldRefuseCreateAndDrop()
    {
        // Arrange
        var builder = SqlDatabaseEngine.CreateBuilder("native-collision");
        builder.Functions.Add(SqlScalarFunction.Create("native_echo", static (long value) => value));
        builder.Types.Add(SqlType.CreateDomain("native_domain", SqlType.BigInt));
        await using var engine = await builder.BuildAsync();
        await using var session = await SessionAsync(engine);
        // Act + Assert
        (await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, "CREATE FUNCTION native_echo(p BIGINT) RETURNS BIGINT RETURN p"))).Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS03065");
        (await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, "CREATE DOMAIN native_domain AS BIGINT"))).Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "CS03301");
        (await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, "DROP FUNCTION native_echo(BIGINT)"))).Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS03303");
        (await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, "DROP DOMAIN native_domain"))).Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS03303");
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - A volatile SQL default executes once per omitted row value")]
    public async Task ExecuteAsync_VolatileDefault_ShouldEvaluatePerRow()
    {
        // Arrange
        int calls = 0;
        await using var engine = await BuildAsync(functions => functions.Add(SqlScalarFunction.Create("sequence_value", (int _) => ++calls)));
        await using var session = await SessionAsync(engine);
        await ExecuteAsync(session, "CREATE FUNCTION next_value() RETURNS INT RETURN sequence_value(0)");
        // Act
        await ExecuteAsync(session, "CREATE TABLE t (id INT, n INT DEFAULT next_value())");
        calls.ShouldBe(0);
        await ExecuteAsync(session, "INSERT INTO t (id) VALUES (1), (2), (3)");
        // Assert
        (await RowsAsync(session, "SELECT n FROM t ORDER BY id")).Select(row => row[0]).ShouldBe(new object?[] { 1, 2, 3 });
        calls.ShouldBe(3);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - SQL definition views retain transaction snapshot metadata")]
    public async Task ExecuteAsync_DefinitionViews_ShouldRetainBeginTimeSnapshot()
    {
        // Arrange
        await using var engine = await BuildAsync(_ => { });
        var database = await engine.CreateDatabaseAsync("functions");
        await using var reader = await database.CreateSessionAsync();
        await using var writer = await database.CreateSessionAsync();
        await ExecuteAsync(writer, "CREATE FUNCTION retained(p INT) RETURNS INT RETURN p");
        await ExecuteAsync(writer, "CREATE DOMAIN retained_type AS INT");
        var transaction = await reader.BeginTransactionAsync(Transactions.IsolationLevel.Snapshot, CancellationToken.None);
        // Act
        await ExecuteAsync(writer, "DROP FUNCTION retained(INT)");
        await ExecuteAsync(writer, "DROP DOMAIN retained_type");
        // Assert
        (await RowsAsync(reader, "SELECT FUNCTION_NAME FROM COHESION_SCHEMA.FUNCTIONS WHERE IS_SQL_DEFINED = 'YES'")).ShouldBe([["retained"]]);
        (await RowsAsync(reader, "SELECT DOMAIN_NAME FROM COHESION_SCHEMA.DOMAINS WHERE IS_SQL_DEFINED = 'YES'")).ShouldBe([["retained_type"]]);
        await transaction.RollbackAsync();
        (await RowsAsync(reader, "SELECT FUNCTION_NAME FROM COHESION_SCHEMA.FUNCTIONS WHERE IS_SQL_DEFINED = 'YES'")).ShouldBeEmpty();
        (await RowsAsync(reader, "SELECT DOMAIN_NAME FROM COHESION_SCHEMA.DOMAINS WHERE IS_SQL_DEFINED = 'YES'")).ShouldBeEmpty();
    }

    private static async Task<SqlDatabaseEngine> BuildAsync(Action<SqlFunctionCollection> register, string? rootPath = null, string? declare = null)
    {
        var builder = SqlDatabaseEngine.CreateBuilder("sql-functions");
        if (rootPath is not null)
        {
            builder.Options.RootPath = rootPath;
        }

        register(builder.Functions);
        if (declare is not null)
        {
            builder.AddDatabase(declare);
        }

        return await builder.BuildAsync();
    }

    private static async Task<SqlDatabaseSession> SessionAsync(SqlDatabaseEngine engine)
    {
        var database = await engine.CreateDatabaseAsync("functions");
        return await database.CreateSessionAsync(CancellationToken.None);
    }

    private static Task<DatabaseResult> ExecuteAsync(SqlDatabaseSession session, string sql, IReadOnlyDictionary<string, object?>? parameters = null)
        => session.ExecuteAsync(sql, parameters, CancellationToken.None).AsTask();

    private static async Task<List<object?[]>> RowsAsync(SqlDatabaseSession session, string sql,
        IReadOnlyDictionary<string, object?>? parameters = null)
    {
        await using var result = (await ExecuteAsync(session, sql, parameters)).ShouldBeAssignableTo<DatabaseResultSet>();
        var rows = new List<object?[]>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        {
            var values = new object?[row.FieldCount];
            for (int i = 0; i < values.Length; i++)
            {
                values[i] = row.GetValue(i);
            }
            rows.Add(values);
        }
        return rows;
    }

}
