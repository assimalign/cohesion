using System;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Connections.InMemory;
using Assimalign.Cohesion.Database.Client;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Testing;

namespace Assimalign.Cohesion.Database.Sql.Client.Tests;

[Collection("Function cancellation")]
public sealed class SqlFunctionCancellationTests
{
    public static IEnumerable<object[]> Cases => CancellationFunctions.Cases();
    public static IEnumerable<object[]> TransactionCases => CancellationFunctions.TransactionCases();
    public static IEnumerable<object[]> DefinitionCases => CancellationFunctions.DefinitionCases();

    [Theory(DisplayName = "Cohesion Test [Database.Sql.Client] - Function cancellation: uncanceled statements get a coded error and keep their session")]
    [MemberData(nameof(Cases))]
    public async Task ExecuteAsync_UncanceledStatement_ShouldCodeApplicationCancellation(string sql, string function)
    {
        // Arrange
        var functions = new CancellationFunctions();
        var builder = SqlDatabaseEngine.CreateBuilder("function-cancellation");
        functions.Register(builder);
        await using var engine = await builder.BuildAsync();
        var database = await engine.CreateDatabaseAsync("db");
        await using var session = await database.CreateSessionAsync();
        await CancellationFunctions.PrepareAsync(session);
        functions.Reset();
        using var statement = new CancellationTokenSource();

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, sql, statement.Token));

        // Assert
        failure.Message.ShouldStartWith($"COHSQLE007: Function '{function}' failed:", Case.Sensitive);
        failure.InnerException.ShouldBeSameAs(functions.Failure);
        functions.Calls.ShouldBeGreaterThan(0);
        functions.LastToken.ShouldBe(statement.Token);
        functions.Tokens.ShouldAllBe(token => token == statement.Token);
        statement.IsCancellationRequested.ShouldBeFalse();
        await ExecuteAsync(session, "SELECT id FROM t", CancellationToken.None);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql.Client] - Function cancellation: canceled statements preserve cancellation through every function phase")]
    [MemberData(nameof(Cases))]
    public async Task ExecuteAsync_CanceledStatement_ShouldPreserveCancellation(string sql, string function)
    {
        // Arrange
        var functions = new CancellationFunctions();
        var builder = SqlDatabaseEngine.CreateBuilder("function-cancellation");
        functions.Register(builder);
        await using var engine = await builder.BuildAsync();
        var database = await engine.CreateDatabaseAsync("db");
        await using var session = await database.CreateSessionAsync();
        await CancellationFunctions.PrepareAsync(session);
        functions.Reset();
        using var statement = new CancellationTokenSource();
        functions.BeforeThrow = _ => statement.Cancel();

        // Act
        var failure = await Should.ThrowAsync<OperationCanceledException>(() => ExecuteAsync(session, sql, statement.Token));

        // Assert
        failure.ShouldBeAssignableTo<OperationCanceledException>(function);
        functions.LastToken.ShouldBe(statement.Token);
        functions.Tokens.ShouldAllBe(token => token == statement.Token);
        functions.Calls.ShouldBe(1);
        statement.IsCancellationRequested.ShouldBeTrue();
        await ExecuteAsync(session, "SELECT id FROM t", CancellationToken.None);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql.Client] - Function cancellation: application faults are coded over the wire on a usable connection")]
    [MemberData(nameof(Cases))]
    public async Task ExecuteAsync_UncanceledWireStatement_ShouldKeepConnectionUsable(string sql, string function)
    {
        // Arrange
        var functions = new CancellationFunctions();
        var builder = SqlDatabaseEngine.CreateBuilder("function-cancellation");
        functions.Register(builder);
        await using var engine = await builder.BuildAsync();
        var database = await engine.CreateDatabaseAsync("db");
        await using (var session = await database.CreateSessionAsync())
        {
            await CancellationFunctions.PrepareAsync(session);
        }
        functions.Reset();
        await using var listener = new InMemoryConnectionListener();
        await using var server = SqlDatabaseServer.Create(engine, new SqlDatabaseServerOptions { Listener = listener });
        await server.StartAsync();
        await using var client = CreateClient(listener);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var connection = await client.ConnectAsync(timeout.Token);

        // Act
        var failure = await Should.ThrowAsync<SqlClientException>(async () =>
            await connection.ExecuteAsync(sql, CancellationFunctions.Parameters, timeout.Token));

        // Assert
        failure.Kind.ShouldBe(SqlClientErrorKind.ExecutionFailure);
        failure.Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);
        failure.Message.ShouldStartWith($"COHSQLE007: Function '{function}' failed:", Case.Sensitive);
        failure.ConnectionUsable.ShouldBeTrue();
        functions.Calls.ShouldBeGreaterThan(0);
        functions.LastToken.IsCancellationRequested.ShouldBeFalse();
        functions.Tokens.ShouldAllBe(token => token.CanBeCanceled && token == functions.LastToken);
        (await connection.QueryAsync("SELECT id FROM t", cancellationToken: timeout.Token)).ShouldHaveSingleItem()[0].ShouldBe(1);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql.Client] - Function cancellation: aborting the server statement remains cancellation over the wire")]
    [MemberData(nameof(Cases))]
    public async Task ExecuteAsync_CanceledWireStatement_ShouldRemainCancellation(string sql, string function)
    {
        // Arrange
        var functions = new CancellationFunctions();
        var builder = SqlDatabaseEngine.CreateBuilder("function-cancellation");
        functions.Register(builder);
        await using var engine = await builder.BuildAsync();
        var database = await engine.CreateDatabaseAsync("db");
        await using (var session = await database.CreateSessionAsync())
        {
            await CancellationFunctions.PrepareAsync(session);
        }
        functions.Reset();
        await using var listener = new InMemoryConnectionListener();
        await using var server = SqlDatabaseServer.Create(engine, new SqlDatabaseServerOptions { Listener = listener });
        await server.StartAsync();
        await using var client = CreateClient(listener);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var connection = await client.ConnectAsync(timeout.Token);
        var serverSession = server.Sessions.ShouldHaveSingleItem();
        using var events = new EventSourceRecorder(EventSource.GetSources().Single(source =>
            source.Name == "Assimalign.Cohesion.Database.Sql"), EventLevel.Verbose);
        var closing = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        functions.BeforeThrow = _ =>
        {
            // Disposal synchronously cancels the server statement, then joins its pump.
            // Join after the core unwinds, never from inside the pump itself.
            Task completion = serverSession.DisposeAsync().AsTask();
            closing.TrySetResult(completion);
        };

        // Act
        var failure = await Should.ThrowAsync<SqlClientException>(async () =>
            await connection.ExecuteAsync(sql, CancellationFunctions.Parameters, timeout.Token));
        Task closedSession = await closing.Task.WaitAsync(timeout.Token);
        await closedSession.WaitAsync(timeout.Token);

        // Assert: the existing wire contract closes a canceled session, without a coded function error.
        failure.ConnectionUsable.ShouldBeFalse();
        functions.Calls.ShouldBe(1);
        functions.Tokens.ShouldAllBe(token => token.CanBeCanceled && token == functions.LastToken);
        functions.LastToken.IsCancellationRequested.ShouldBeTrue();
        var closed = events.Events.Single(item => item.EventName == "SessionClosed" &&
            Equals(item.Payload![0], serverSession.Id));
        closed.Payload![1].ShouldBe("Cancelled", function);
        events.Events.ShouldNotContain(item => item.EventName == "SessionFaulted");
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql.Client] - Function cancellation: explicit transactions commit earlier and later writes without failed statement writes")]
    [MemberData(nameof(TransactionCases))]
    public async Task ExecuteAsync_UncanceledWireTransaction_ShouldCommitSurroundingWrites(string sql, string function)
    {
        // Arrange
        var functions = new CancellationFunctions();
        var builder = SqlDatabaseEngine.CreateBuilder("function-cancellation");
        functions.Register(builder);
        await using var engine = await builder.BuildAsync();
        var database = await engine.CreateDatabaseAsync("db");
        await using (var session = await database.CreateSessionAsync())
        {
            await CancellationFunctions.PrepareAsync(session);
        }
        functions.Reset();
        await using var listener = new InMemoryConnectionListener();
        await using var server = SqlDatabaseServer.Create(engine, new SqlDatabaseServerOptions { Listener = listener });
        await server.StartAsync();
        await using var client = CreateClient(listener);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var connection = await client.ConnectAsync(timeout.Token);
        await connection.ExecuteAsync("BEGIN", cancellationToken: timeout.Token);
        await connection.ExecuteAsync("INSERT INTO t VALUES (10)", cancellationToken: timeout.Token);

        // Act
        var failure = await Should.ThrowAsync<SqlClientException>(async () =>
            await connection.ExecuteAsync(sql, CancellationFunctions.Parameters, timeout.Token));
        await connection.ExecuteAsync("INSERT INTO t VALUES (30)", cancellationToken: timeout.Token);
        var pending = await connection.QueryAsync("SELECT id FROM t ORDER BY id", cancellationToken: timeout.Token);
        await connection.ExecuteAsync("COMMIT", cancellationToken: timeout.Token);
        await using var observer = await client.ConnectAsync(timeout.Token);
        var committed = await observer.QueryAsync("SELECT id FROM t ORDER BY id", cancellationToken: timeout.Token);

        // Assert
        failure.Kind.ShouldBe(SqlClientErrorKind.ExecutionFailure);
        failure.Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);
        failure.Message.ShouldStartWith($"COHSQLE007: Function '{function}' failed:", Case.Sensitive);
        failure.ConnectionUsable.ShouldBeTrue();
        functions.Calls.ShouldBeGreaterThan(0);
        functions.Tokens.ShouldAllBe(token => token.CanBeCanceled && !token.IsCancellationRequested);
        pending.Select(row => row[0]).ShouldBe(new object[] { 1, 10, 30 });
        committed.Select(row => row[0]).ShouldBe(new object[] { 1, 10, 30 });
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql.Client] - Shared definitions: folding uses the binding token and later execution uses its own token")]
    [MemberData(nameof(DefinitionCases))]
    public async Task ExecuteAsync_SharedDefinition_ShouldUseCurrentOperationToken(string? setup, string declaration, string sql, string function, bool canceled)
    {
        // Arrange
        var functions = new CancellationFunctions();
        var builder = SqlDatabaseEngine.CreateBuilder("function-cancellation");
        functions.Register(builder);
        await using var engine = await builder.BuildAsync();
        var database = await engine.CreateDatabaseAsync("db");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE t (id INT)");
        await session.ExecuteAsync("INSERT INTO t VALUES (1)");
        if (setup is not null) { await session.ExecuteAsync(setup); }
        functions.Reset();
        using var binding = new CancellationTokenSource();
        using var statement = new CancellationTokenSource();
        if (canceled) { functions.BeforeThrow = _ => binding.Cancel(); }

        // Act
        if (canceled)
        {
            await Should.ThrowAsync<OperationCanceledException>(() => ExecuteAsync(session, declaration, binding.Token));
        }
        else
        {
            await ExecuteAsync(session, declaration, binding.Token);
        }

        // Assert: every attempt to fold has the operation's token, even if its coded error is deferred.
        functions.Calls.ShouldBeGreaterThan(0);
        functions.Tokens.ShouldAllBe(token => token == binding.Token);
        binding.IsCancellationRequested.ShouldBe(canceled);
        if (canceled) { functions.Calls.ShouldBe(1); return; }

        // Act: a failed fold must not retain the binding token in the cached expression.
        functions.Reset();
        var failure = await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, sql, statement.Token));

        // Assert
        failure.Message.ShouldStartWith($"COHSQLE007: Function '{function}' failed:", Case.Sensitive);
        functions.Calls.ShouldBeGreaterThan(0);
        functions.Tokens.ShouldAllBe(token => token == statement.Token);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql.Client] - Shared definitions: wire binding observes cancellation and preserves coded failures for later execution")]
    [MemberData(nameof(DefinitionCases))]
    public async Task ExecuteAsync_SharedWireDefinition_ShouldUseCurrentOperationToken(string? setup, string declaration, string sql, string function, bool canceled)
    {
        // Arrange
        var functions = new CancellationFunctions();
        var builder = SqlDatabaseEngine.CreateBuilder("function-cancellation");
        functions.Register(builder);
        await using var engine = await builder.BuildAsync();
        var database = await engine.CreateDatabaseAsync("db");
        await using (var session = await database.CreateSessionAsync())
        {
            await session.ExecuteAsync("CREATE TABLE t (id INT)");
            await session.ExecuteAsync("INSERT INTO t VALUES (1)");
            if (setup is not null) { await session.ExecuteAsync(setup); }
        }
        functions.Reset();
        await using var listener = new InMemoryConnectionListener();
        await using var server = SqlDatabaseServer.Create(engine, new SqlDatabaseServerOptions { Listener = listener });
        await server.StartAsync();
        await using var client = CreateClient(listener);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var connection = await client.ConnectAsync(timeout.Token);
        var serverSession = server.Sessions.ShouldHaveSingleItem();
        Task? closing = null;
        if (canceled) { functions.BeforeThrow = _ => closing = serverSession.DisposeAsync().AsTask(); }

        // Act
        if (canceled)
        {
            var failure = await Should.ThrowAsync<SqlClientException>(async () =>
                await connection.ExecuteAsync(declaration, cancellationToken: timeout.Token));
            await closing.ShouldNotBeNull().WaitAsync(timeout.Token);
            failure.ConnectionUsable.ShouldBeFalse();
        }
        else
        {
            await connection.ExecuteAsync(declaration, cancellationToken: timeout.Token);
        }

        // Assert
        functions.Calls.ShouldBeGreaterThan(0);
        functions.Tokens.ShouldAllBe(token => token.CanBeCanceled && token == functions.LastToken);
        functions.LastToken.IsCancellationRequested.ShouldBe(canceled);
        if (canceled) { functions.Calls.ShouldBe(1); return; }

        // Act
        functions.Reset();
        var coded = await Should.ThrowAsync<SqlClientException>(async () =>
            await connection.ExecuteAsync(sql, cancellationToken: timeout.Token));

        // Assert
        coded.Message.ShouldStartWith($"COHSQLE007: Function '{function}' failed:", Case.Sensitive);
        coded.ConnectionUsable.ShouldBeTrue();
        functions.Calls.ShouldBeGreaterThan(0);
        functions.Tokens.ShouldAllBe(token => token.CanBeCanceled && !token.IsCancellationRequested);
        (await connection.QueryAsync("SELECT id FROM t", cancellationToken: timeout.Token)).ShouldHaveSingleItem()[0].ShouldBe(1);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql.Client] - Shared SQL bodies: a canceled collation binding does not poison the next statement")]
    [InlineData("SELECT collated_body('x' COLLATE case_insensitive) FROM t", "cancel_fixed")]
    [InlineData("SELECT collated_argument('x' COLLATE case_insensitive) FROM t", "cancel_check")]
    [InlineData("SELECT collated_result('x' COLLATE case_insensitive) FROM t", "cancel_check")]
    public async Task ExecuteAsync_CanceledCollationBinding_ShouldAllowRetryWithNewToken(string sql, string function)
    {
        // Arrange
        var functions = new CancellationFunctions();
        var builder = SqlDatabaseEngine.CreateBuilder("function-cancellation");
        functions.Register(builder);
        await using var engine = await builder.BuildAsync();
        var database = await engine.CreateDatabaseAsync("db");
        await using var session = await database.CreateSessionAsync();
        await CancellationFunctions.PrepareAsync(session);
        functions.Reset();
        using var first = new CancellationTokenSource();
        using var second = new CancellationTokenSource();
        functions.BeforeThrow = _ => first.Cancel();

        // Act
        await Should.ThrowAsync<OperationCanceledException>(() => ExecuteAsync(session, sql, first.Token));

        // Assert
        functions.Calls.ShouldBe(1);
        functions.LastToken.ShouldBe(first.Token);

        // Act
        functions.Reset();
        functions.BeforeThrow = null;
        var failure = await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, sql, second.Token));

        // Assert
        failure.Message.ShouldStartWith($"COHSQLE007: Function '{function}' failed:", Case.Sensitive);
        functions.Calls.ShouldBeGreaterThan(0);
        functions.Tokens.ShouldAllBe(token => token == second.Token);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql.Client] - Shared definitions: cancellation while binding a published definition allows the next statement to reload it")]
    [InlineData("CREATE FUNCTION shared_body(p INT) RETURNS INT RETURN cancel_fixed(1)", "SELECT shared_body(id) FROM t", "cancel_fixed")]
    [InlineData("CREATE DOMAIN shared_domain AS INT CHECK (cancel_check(1))", "SELECT CAST(id AS shared_domain) FROM t", "cancel_check")]
    public async Task ExecuteAsync_CanceledDefinitionRefresh_ShouldReloadWithNextStatementToken(string declaration, string sql, string function)
    {
        // Arrange
        var functions = new CancellationFunctions();
        var builder = SqlDatabaseEngine.CreateBuilder("function-cancellation");
        functions.Register(builder);
        await using var engine = await builder.BuildAsync();
        var database = await engine.CreateDatabaseAsync("db");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE t (id INT)");
        await session.ExecuteAsync("INSERT INTO t VALUES (1)");
        using var binding = new CancellationTokenSource();
        using var statement = new CancellationTokenSource();
        functions.BeforeThrow = _ => binding.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => ExecuteAsync(session, declaration, binding.Token));
        functions.Reset();
        functions.BeforeThrow = null;

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, sql, statement.Token));

        // Assert
        failure.Message.ShouldStartWith($"COHSQLE007: Function '{function}' failed:", Case.Sensitive);
        functions.Calls.ShouldBeGreaterThan(0);
        functions.Tokens.ShouldAllBe(token => token == statement.Token);
    }

    private static SqlClient CreateClient(InMemoryConnectionListener listener)
        => SqlClient.Create(new SqlClientOptions
        {
            Settings = new DatabaseConnectionSettings { Database = "db", Principal = "tester", EndPoint = listener.EndPoint },
            ConnectionFactory = listener.CreateFactory(),
        });

    private static async Task ExecuteAsync(SqlDatabaseSession session, string sql, CancellationToken cancellationToken)
    {
        var result = await session.ExecuteAsync(sql, CancellationFunctions.Parameters, cancellationToken);
        if (result is QueryResultSet rows)
        {
            await using (rows)
            {
                await foreach (var row in rows.GetRowsAsync(cancellationToken))
                {
                    _ = row.FieldCount;
                }
            }
        }
    }
}
