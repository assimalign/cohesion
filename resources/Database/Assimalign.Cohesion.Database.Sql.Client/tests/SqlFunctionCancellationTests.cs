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
        using var statement = new CancellationTokenSource();

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, sql, statement.Token));

        // Assert
        failure.Message.ShouldStartWith($"COHSQLE007: Function '{function}' failed:", Case.Sensitive);
        failure.InnerException.ShouldBeSameAs(functions.Failure);
        functions.Calls.ShouldBeGreaterThan(0);
        functions.LastToken.ShouldBe(statement.Token);
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
        using var statement = new CancellationTokenSource();
        functions.BeforeThrow = token =>
        {
            // Folding has no statement context; its failed call is retried during execution.
            if (token.CanBeCanceled)
            {
                statement.Cancel();
            }
        };

        // Act
        var failure = await Should.ThrowAsync<OperationCanceledException>(() => ExecuteAsync(session, sql, statement.Token));

        // Assert
        failure.ShouldBeAssignableTo<OperationCanceledException>(function);
        functions.LastToken.ShouldBe(statement.Token);
        functions.Calls.ShouldBeGreaterThan(0);
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
        await using var listener = new InMemoryConnectionListener();
        await using var server = SqlDatabaseServer.Create(engine, new SqlDatabaseServerOptions { Listener = listener });
        await server.StartAsync();
        await using var client = CreateClient(listener);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var connection = await client.ConnectAsync(timeout.Token);

        // Act
        var failure = await Should.ThrowAsync<SqlClientException>(async () =>
            await connection.ExecuteAsync(sql, cancellationToken: timeout.Token));

        // Assert
        failure.Kind.ShouldBe(SqlClientErrorKind.ExecutionFailure);
        failure.Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);
        failure.Message.ShouldStartWith($"COHSQLE007: Function '{function}' failed:", Case.Sensitive);
        failure.ConnectionUsable.ShouldBeTrue();
        functions.Calls.ShouldBeGreaterThan(0);
        functions.LastToken.IsCancellationRequested.ShouldBeFalse();
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
        functions.BeforeThrow = token =>
        {
            if (token.CanBeCanceled)
            {
                // Disposal synchronously cancels the server statement, then joins its pump.
                // Join after the core unwinds, never from inside the pump itself.
                Task completion = serverSession.DisposeAsync().AsTask();
                token.IsCancellationRequested.ShouldBeTrue();
                closing.SetResult(completion);
            }
        };

        // Act
        var failure = await Should.ThrowAsync<SqlClientException>(async () =>
            await connection.ExecuteAsync(sql, cancellationToken: timeout.Token));
        Task closedSession = await closing.Task.WaitAsync(timeout.Token);
        await closedSession.WaitAsync(timeout.Token);

        // Assert: the existing wire contract closes a canceled session, without a coded function error.
        failure.ConnectionUsable.ShouldBeFalse();
        functions.Calls.ShouldBeGreaterThan(0);
        functions.LastToken.IsCancellationRequested.ShouldBeTrue();
        var closed = events.Events.Single(item => item.EventName == "SessionClosed" &&
            Equals(item.Payload![0], serverSession.Id));
        closed.Payload![1].ShouldBe("Cancelled", function);
        events.Events.ShouldNotContain(item => item.EventName == "SessionFaulted");
    }

    private static SqlClient CreateClient(InMemoryConnectionListener listener)
        => SqlClient.Create(new SqlClientOptions
        {
            Settings = new DatabaseConnectionSettings { Database = "db", Principal = "tester", EndPoint = listener.EndPoint },
            ConnectionFactory = listener.CreateFactory(),
        });

    private static async Task ExecuteAsync(SqlDatabaseSession session, string sql, CancellationToken cancellationToken)
    {
        var result = await session.ExecuteAsync(sql, null, cancellationToken);
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
