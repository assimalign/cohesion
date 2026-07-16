using System;
using System.Linq;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Client;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Client.Tests;

/// <summary>
/// End-to-end tests for the typed SQL client (#180): commands, typed result sets,
/// the SQL-scoped error surface, and the telemetry hook — over the real server and a
/// live SQL engine on the in-memory driver.
/// </summary>
public class SqlClientTests
{
    [Fact(DisplayName = "Cohesion Test [Database.Sql.Client] - Query: SELECT materializes typed columns and rows")]
    public async Task QueryAsync_Select_ShouldMaterializeTypedColumnsAndRows()
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());

        // Act
        SqlResultSet result = await connection.QueryAsync("SELECT id, name FROM users ORDER BY id", cancellationToken: SqlClientTestHarness.Timeout());

        // Assert
        result.Columns.Count.ShouldBe(2);
        result.Columns[0].ShouldBe(new SqlColumn("id", 0, DatabaseType.Int32));
        result.Columns[1].ShouldBe(new SqlColumn("name", 1, DatabaseType.String));

        result.Count.ShouldBe(2);
        result[0].GetInt32("id").ShouldBe(1);
        result[0].GetString("name").ShouldBe("ada");
        result[1].GetInt32(0).ShouldBe(2);
        result[1].GetString(1).ShouldBe("grace");
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql.Client] - Query: typed getters widen numeric columns")]
    public async Task QueryAsync_TypedGetters_ShouldWidenNumericValues()
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());

        // Act
        SqlResultSet result = await connection.QueryAsync("SELECT id, score FROM users WHERE id = 2", cancellationToken: SqlClientTestHarness.Timeout());

        // Assert: an Int32 id widens to long; a BIGINT score reads as long
        SqlRow row = result.ShouldHaveSingleItem();
        row.GetInt64("id").ShouldBe(2L);
        row.GetInt64("score").ShouldBe(200L);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql.Client] - Command: a command with bound parameters round-trips")]
    public async Task QueryAsync_CommandWithParameters_ShouldBindValues()
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());

        var command = new SqlCommand("SELECT name FROM users WHERE id = @id").WithParameter("@id", 2);

        // Act
        SqlResultSet result = await connection.QueryAsync(command, SqlClientTestHarness.Timeout());

        // Assert (the '@' sigil is normalized away when binding)
        result.ShouldHaveSingleItem().GetString("name").ShouldBe("grace");
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql.Client] - Execute: a non-query command returns the affected count")]
    public async Task ExecuteAsync_Insert_ShouldReturnAffectedCount()
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());

        // Act
        long affected = await connection.ExecuteAsync("INSERT INTO users (id, name, score) VALUES (5, 'lin', 50)", cancellationToken: SqlClientTestHarness.Timeout());

        // Assert
        affected.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql.Client] - Scalar: ExecuteScalar returns the first column of the first row")]
    public async Task ExecuteScalarAsync_Aggregate_ShouldReturnFirstValue()
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());

        // Act
        long count = await connection.ExecuteScalarAsync<long>(new SqlCommand("SELECT COUNT(*) FROM users"), SqlClientTestHarness.Timeout());

        // Assert
        count.ShouldBe(2L);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql.Client] - Rows: null values report through IsNull")]
    public async Task QueryAsync_NullColumn_ShouldReportIsNull()
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());
        await connection.ExecuteAsync("INSERT INTO users (id, name) VALUES (9, 'noscore')", cancellationToken: SqlClientTestHarness.Timeout());

        // Act
        SqlResultSet result = await connection.QueryAsync("SELECT score FROM users WHERE id = 9", cancellationToken: SqlClientTestHarness.Timeout());

        // Assert
        result.ShouldHaveSingleItem().IsNull("score").ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql.Client] - Rows: an unknown column name is rejected")]
    public async Task QueryAsync_UnknownColumnName_ShouldThrow()
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());
        SqlResultSet result = await connection.QueryAsync("SELECT id FROM users WHERE id = 1", cancellationToken: SqlClientTestHarness.Timeout());

        // Act / Assert
        Should.Throw<ArgumentException>(() => result[0].GetInt32("missing"));
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql.Client] - Errors: statement failures map to a SQL error kind and keep the connection usable")]
    public async Task ExecuteAsync_StatementFailures_ShouldMapKindAndSurviveConnection()
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());

        // Act: an unknown command → parse failure; a missing table → execution failure
        SqlClientException parseFailure = await Should.ThrowAsync<SqlClientException>(
            async () => await connection.ExecuteAsync("TRUNCATE TABLE users;", cancellationToken: SqlClientTestHarness.Timeout()));

        SqlClientException executionFailure = await Should.ThrowAsync<SqlClientException>(
            async () => await connection.QueryAsync("SELECT id FROM missing_table", cancellationToken: SqlClientTestHarness.Timeout()));

        // Assert: kinds map, the failures are flagged connection-safe, and the connection still works
        parseFailure.Kind.ShouldBe(SqlClientErrorKind.ParseFailure);
        parseFailure.ConnectionUsable.ShouldBeTrue();
        executionFailure.Kind.ShouldBe(SqlClientErrorKind.ExecutionFailure);
        connection.IsOpen.ShouldBeTrue();

        SqlResultSet stillWorks = await connection.QueryAsync("SELECT id FROM users WHERE id = 1", cancellationToken: SqlClientTestHarness.Timeout());
        stillWorks.ShouldHaveSingleItem();
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql.Client] - Telemetry: the observer sees executing/executed around a command")]
    public async Task QueryAsync_WithObserver_ShouldNotifyExecutingAndExecuted()
    {
        // Arrange
        var observer = new RecordingObserver();
        await using var harness = await SqlClientTestHarness.StartAsync(observer: observer);
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());

        // Act
        await connection.QueryAsync("SELECT id FROM users ORDER BY id", cancellationToken: SqlClientTestHarness.Timeout());

        // Assert
        observer.Executing.ShouldContain("SELECT id FROM users ORDER BY id");
        var executed = observer.Executed.ShouldHaveSingleItem();
        executed.RowCount.ShouldBe(2);
        executed.AffectedCount.ShouldBe(-1);
        observer.Failed.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql.Client] - Telemetry: the observer sees a command failure")]
    public async Task ExecuteAsync_FailingCommand_ShouldNotifyObserverFailure()
    {
        // Arrange
        var observer = new RecordingObserver();
        await using var harness = await SqlClientTestHarness.StartAsync(observer: observer);
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());

        // Act
        await Should.ThrowAsync<SqlClientException>(
            async () => await connection.QueryAsync("SELECT id FROM missing_table", cancellationToken: SqlClientTestHarness.Timeout()));

        // Assert
        observer.Failed.ShouldHaveSingleItem().Kind.ShouldBe(SqlClientErrorKind.ExecutionFailure);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql.Client] - Pooling: reconnecting reuses the authenticated session")]
    public async Task ConnectAsync_AfterDispose_ShouldReuseSession()
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();

        // Act: connect, query, dispose (returns to pool); then connect again
        var first = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());
        await first.QueryAsync("SELECT id FROM users WHERE id = 1", cancellationToken: SqlClientTestHarness.Timeout());
        await first.DisposeAsync();

        var second = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());
        await second.QueryAsync("SELECT id FROM users WHERE id = 2", cancellationToken: SqlClientTestHarness.Timeout());
        await second.DisposeAsync();

        // Assert: only one server session ever existed — the pool reused it
        harness.Server.Context.Sessions.ShouldHaveSingleItem();
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql.Client] - Options: creation validates settings and factory")]
    public void Create_WithIncompleteOptions_ShouldThrow()
    {
        // Act / Assert
        Should.Throw<ArgumentNullException>(() => SqlClient.Create(null!));
        Should.Throw<ArgumentException>(() => SqlClient.Create(new SqlClientOptions()));
        Should.Throw<ArgumentException>(() => SqlClient.Create(new SqlClientOptions
        {
            Settings = new DatabaseConnectionSettings { Database = "app" }, // no factory
        }));
    }
}
