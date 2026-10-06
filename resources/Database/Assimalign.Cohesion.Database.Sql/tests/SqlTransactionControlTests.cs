using System.Linq;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed class SqlTransactionControlTests
{
    [Fact]
    public async Task WireCommit_ShouldPublishMultipleStatementsTogether()
    {
        await using var harness = await ServerTestHarness.StartAsync();
        await using var writer = await harness.DialAsync();
        await using var observer = await harness.DialAsync();
        await writer.HandshakeAsync();
        await observer.HandshakeAsync();

        await ExecuteAsync(writer, "BEGIN TRANSACTION");
        await ExecuteAsync(writer, "INSERT INTO users VALUES (3, 'lin')");
        await ExecuteAsync(writer, "INSERT INTO users VALUES (4, 'ed')");
        (await CountAsync(writer)).ShouldBe(4);
        (await CountAsync(observer)).ShouldBe(2);

        await ExecuteAsync(writer, "COMMIT TRANSACTION");
        (await CountAsync(observer)).ShouldBe(4);
        harness.Server.Sessions.ShouldAllBe(s => s.DatabaseSession!.CurrentTransaction == null);
    }

    [Fact]
    public async Task WireRollback_ShouldUndoInsertUpdateAndDelete()
    {
        await using var harness = await ServerTestHarness.StartAsync();
        await using var writer = await harness.DialAsync();
        await using var observer = await harness.DialAsync();
        await writer.HandshakeAsync();
        await observer.HandshakeAsync();

        await ExecuteAsync(writer, "BEGIN");
        await ExecuteAsync(writer, "INSERT INTO users VALUES (3, 'lin'), (4, 'ed')");
        await ExecuteAsync(writer, "UPDATE users SET name = 'changed' WHERE id = 1");
        await ExecuteAsync(writer, "DELETE FROM users WHERE id = 2");
        (await CountAsync(writer)).ShouldBe(3);
        (await CountAsync(observer, "name = 'changed'")).ShouldBe(0);
        await ExecuteAsync(writer, "ROLLBACK TRANSACTION");

        (await CountAsync(observer)).ShouldBe(2);
        (await CountAsync(observer, "name = 'ada'")).ShouldBe(1);
        (await CountAsync(observer, "id = 2")).ShouldBe(1);
        await ExecuteAsync(writer, "BEGIN");
        await ExecuteAsync(writer, "ROLLBACK");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WireDisconnect_ShouldAbortOpenTransaction(bool terminate)
    {
        await using var harness = await ServerTestHarness.StartAsync();
        await using var observer = await harness.DialAsync();
        await observer.HandshakeAsync();
        var writer = await harness.DialAsync();
        await writer.HandshakeAsync();
        await ExecuteAsync(writer, "BEGIN");
        var transaction = harness.Server.Sessions
            .Where(s => s.DatabaseSession!.CurrentTransaction != null).ShouldHaveSingleItem()
            .DatabaseSession!.CurrentTransaction!;
        await ExecuteAsync(writer, "INSERT INTO users VALUES (3, 'lin')");
        await ExecuteAsync(writer, "DELETE FROM users WHERE id = 1");

        if (terminate)
        {
            await writer.SendAsync(ProtocolMessageType.Terminate);
            (await writer.ReadAsync()).ShouldBeNull();
        }

        await writer.DisposeAsync();
        await ServerTestHarness.WaitUntilAsync(() => harness.Server.Sessions.Count == 1);
        transaction.State.ShouldBe(TransactionState.RolledBack);
        (await CountAsync(observer)).ShouldBe(2);
        (await CountAsync(observer, "id = 3")).ShouldBe(0);
        (await CountAsync(observer, "id = 1")).ShouldBe(1);
        // The disconnected writer's locks were released as well as its stamps.
        await ExecuteAsync(observer, "DELETE FROM users WHERE id = 1");
    }

    [Fact]
    public async Task TransactionStateErrors_ShouldReturnStableDiagnosticsAndPreserveScope()
    {
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "transaction-diagnostics" });
        var database = await engine.CreateDatabaseAsync("app");
        await using var session = await database.CreateSessionAsync();

        foreach (string command in new[] { "COMMIT", "ROLLBACK TRANSACTION" })
        {
            var result = await session.ExecuteAsync(command);
            result.Status.ShouldBe(QueryResultStatus.Error);
            result.Diagnostics.ShouldNotBeNull().ShouldHaveSingleItem().Code.ShouldBe("COHSQLT002");
        }

        (await session.ExecuteAsync("BEGIN")).Status.ShouldBe(QueryResultStatus.Success);
        var transaction = session.CurrentTransaction.ShouldNotBeNull();
        transaction.IsolationLevel.ShouldBe(IsolationLevel.Snapshot);
        var nested = await session.ExecuteAsync("BEGIN TRANSACTION");
        nested.Status.ShouldBe(QueryResultStatus.Error);
        nested.Diagnostics.ShouldNotBeNull().ShouldHaveSingleItem().Code.ShouldBe("COHSQLT001");
        session.CurrentTransaction.ShouldBeSameAs(transaction);
        await session.ExecuteAsync("ROLLBACK");
        session.CurrentTransaction.ShouldBeNull();

        await using var apiTransaction = await session.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        await session.ExecuteAsync("COMMIT");
        apiTransaction.State.ShouldBe(TransactionState.Committed);
    }

    [Fact]
    public async Task TypedRequestWithParseErrors_ShouldNotExecuteRecoveredTransactionCommand()
    {
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "typed-control" });
        var database = await engine.CreateDatabaseAsync("app");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("BEGIN");
        var transaction = session.CurrentTransaction.ShouldNotBeNull();
        var invalid = (SqlQueryStatement)new SqlQueryParser().Parse("ROLLBACK TO marker");

        var result = await session.ExecuteAsync(new SqlQueryRequest(invalid));
        result.Status.ShouldBe(QueryResultStatus.Error);
        result.Diagnostics.ShouldNotBeNull().ShouldNotBeEmpty();
        transaction.State.ShouldBe(TransactionState.Active);
        session.CurrentTransaction.ShouldBeSameAs(transaction);
        await session.ExecuteAsync("ROLLBACK");
    }

    [Fact]
    public async Task ExplicitTransactionDdl_ShouldFailClosedAndLeaveTransactionUsable()
    {
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "ddl-diagnostics" });
        var database = await engine.CreateDatabaseAsync("app");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE t (id INT)");
        await session.ExecuteAsync("BEGIN");
        var result = await session.ExecuteAsync("DROP TABLE t");
        result.Status.ShouldBe(QueryResultStatus.Error);
        result.Diagnostics.ShouldNotBeNull().ShouldHaveSingleItem().Code.ShouldBe("COHSQLT003");
        (await session.ExecuteAsync("INSERT INTO t VALUES (1)")).AffectedCount.ShouldBe(1);
        await session.ExecuteAsync("ROLLBACK");
        (await session.ExecuteAsync("INSERT INTO t VALUES (2)")).AffectedCount.ShouldBe(1);
    }

    [Fact]
    public async Task WireTransactionStateErrors_ShouldCarryDiagnosticCodeAndKeepConnectionUsable()
    {
        await using var harness = await ServerTestHarness.StartAsync();
        await using var client = await harness.DialAsync();
        await client.HandshakeAsync();

        await ExpectDiagnosticAsync(client, "COMMIT", "COHSQLT002");
        await ExecuteAsync(client, "BEGIN");
        await ExpectDiagnosticAsync(client, "BEGIN", "COHSQLT001");
        await ExecuteAsync(client, "INSERT INTO users VALUES (3, 'lin')");
        await ExecuteAsync(client, "COMMIT");
        (await CountAsync(client)).ShouldBe(3);
    }

    /// <summary>
    /// Over the wire, a transaction the kernel ended under its caller (the served database was
    /// dropped while the connection held it) refuses the connection's statements and its COMMIT with
    /// <c>COHSQLT005</c> as execution failures, the COMMIT ends it, and the connection stays usable
    /// (concrete-types plan §6.4, "Sql at P4"). Before the root bases the session dropped such a
    /// transaction, so the statement ran in auto-commit against the dropped database and the COMMIT
    /// answered <c>COHSQLT002</c>.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Wire transaction: one the kernel ended refuses work and COMMIT with COHSQLT005")]
    public async Task ExecuteAsync_WireTransactionEndedByTheKernel_ShouldCarryCohsqlt005AndKeepConnectionUsable()
    {
        // Arrange
        await using var harness = await ServerTestHarness.StartAsync();
        await using var client = await harness.DialAsync();
        await client.HandshakeAsync();
        await ExecuteAsync(client, "BEGIN");
        await ExecuteAsync(client, "INSERT INTO users VALUES (3, 'lin')");
        var session = harness.Server.Sessions.ShouldHaveSingleItem().DatabaseSession.ShouldNotBeNull();
        var transaction = session.CurrentTransaction.ShouldNotBeNull();
        await harness.Engine.DropDatabaseAsync(ServerTestHarness.DatabaseName);

        // Act
        string statement = await ExpectExecutionFailureAsync(client, "SELECT COUNT(*) FROM users");
        string commit = await ExpectExecutionFailureAsync(client, "COMMIT");
        await client.SendAsync(ProtocolMessageType.Ping);

        // Assert
        await client.ExpectAsync(ProtocolMessageType.Pong);
        statement.ShouldStartWith("COHSQLT005: The session's transaction is aborted; statements are refused until it is rolled back.", Case.Sensitive);
        commit.ShouldStartWith("COHSQLT005: The session's transaction is aborted and cannot commit; nothing was committed.", Case.Sensitive);
        transaction.State.ShouldNotBe(TransactionState.Committed);
        session.CurrentTransaction.ShouldBeNull();
    }

    private static async Task<string> ExpectExecutionFailureAsync(ProtocolTestClient client, string sql)
    {
        await client.SendAsync(ProtocolMessageType.Execute, ProtocolExecuteMessage.Create(sql).Encode());
        var frame = await client.ExpectAsync(ProtocolMessageType.Error);
        var error = ProtocolErrorMessage.Decode(frame.Payload.Span);
        error.Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);
        return error.Message;
    }

    private static async Task ExecuteAsync(ProtocolTestClient client, string sql)
    {
        await client.SendAsync(ProtocolMessageType.Execute, ProtocolExecuteMessage.Create(sql).Encode());
        await client.ExpectAsync(ProtocolMessageType.ResultComplete);
    }

    private static async Task ExpectDiagnosticAsync(ProtocolTestClient client, string sql, string code)
    {
        await client.SendAsync(ProtocolMessageType.Execute, ProtocolExecuteMessage.Create(sql).Encode());
        var frame = await client.ExpectAsync(ProtocolMessageType.Error);
        var error = ProtocolErrorMessage.Decode(frame.Payload.Span);
        error.Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);
        error.Message.ShouldContain(code);
    }

    private static async Task<long> CountAsync(ProtocolTestClient client, string? predicate = null)
    {
        string sql = "SELECT COUNT(*) FROM users" + (predicate is null ? "" : $" WHERE {predicate}");
        await client.SendAsync(ProtocolMessageType.Execute, ProtocolExecuteMessage.Create(sql).Encode());
        await client.ExpectAsync(ProtocolMessageType.ResultHeader);
        var row = await client.ExpectAsync(ProtocolMessageType.ResultRow);
        long count = (long)DatabaseValueCodec.DecodeComponent(row.Payload.Span)!;
        await client.ExpectAsync(ProtocolMessageType.ResultComplete);
        return count;
    }
}
