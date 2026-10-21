using System;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Sql.Client;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Protocol;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Sql.Tests;

[Collection(SqlTimingCollection.Name)]
public sealed class SqlResultDeliveryConformanceTests
{
    [Theory(DisplayName = "Cohesion Test [Database.Sql] - Result preparation: retained metrics release wire buffers after successful and failed writes")]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ExecuteAsync_WriteCompletes_ShouldReleasePreparationFromMetrics(bool explicitTransaction, bool fail)
    {
        // Arrange
        await using var harness = await ServerTestHarness.StartAsync();
        await using var client = SqlClient.Create(new()
        {
            Settings = new() { Database = ServerTestHarness.DatabaseName, EndPoint = harness.Listener.EndPoint, MaxPoolSize = 1 },
            ConnectionFactory = harness.Listener.CreateFactory(),
        });
        await using var connection = await client.ConnectAsync(TestTimeout.Token());
        var serverSession = (SqlDatabaseServerSession)harness.Server.Sessions.ShouldHaveSingleItem();
        if (explicitTransaction) { await connection.ExecuteAsync("BEGIN", cancellationToken: TestTimeout.Token()); }
        if (fail) { serverSession.ExecuteResultOverride = () => new ResultDeliveryFailureSet("value"); }

        // Act
        const string statement = "INSERT INTO users VALUES (5, 'prepared') RETURNING id";
        if (fail)
        {
            var failure = await Should.ThrowAsync<SqlClientException>(async () =>
                await connection.QueryAsync(statement, cancellationToken: TestTimeout.Token()));
            failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHSQLE045");
        }
        else { (await connection.QueryAsync(statement, cancellationToken: TestTimeout.Token())).Count.ShouldBe(1); }

        // Assert: inspect the write's metrics before any later statement can replace them.
        var metrics = serverSession.DatabaseSession!.LastStatementMetrics.ShouldNotBeNull();
        metrics.IsPlanned.ShouldBeTrue();
        metrics.ResultPreparation.ShouldBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql] - Result delivery: read encoding failures end partial results and the same session is reused")]
    [InlineData("value", false)]
    [InlineData("value", true)]
    [InlineData("unicode", false)]
    [InlineData("unicode", true)]
    [InlineData("oversized", false)]
    [InlineData("oversized", true)]
    [InlineData("binary", false)]
    [InlineData("binary", true)]
    [InlineData("enumeration", false)]
    [InlineData("enumeration", true)]
    [InlineData("uncoded", false)]
    [InlineData("uncoded", true)]
    [InlineData("dispose", false)]
    [InlineData("dispose", true)]
    [InlineData("header", false)]
    [InlineData("header", true)]
    public async Task ExecuteAsync_LateResultFailure_ShouldEndStatementAndReuseSession(string failure, bool explicitTransaction)
    {
        // Arrange
        await using var harness = await ServerTestHarness.StartAsync();
        await using var peer = await harness.DialAsync();
        await peer.HandshakeAsync();
        var serverSession = (SqlDatabaseServerSession)harness.Server.Sessions.ShouldHaveSingleItem();
        var transaction = explicitTransaction ? await serverSession.DatabaseSession!.BeginTransactionAsync(TestTimeout.Token()) : null;
        var result = new ResultDeliveryFailureSet(failure);
        serverSession.ExecuteResultOverride = () => result;

        // Act
        await peer.SendAsync((ProtocolMessageType)SqlProtocolMessageType.Execute, ProtocolExecuteMessage.Create("SELECT 1").Encode());
        if (failure != "header")
        {
            await peer.ExpectAsync((ProtocolMessageType)SqlProtocolMessageType.ResultHeader);
            await peer.ExpectAsync((ProtocolMessageType)SqlProtocolMessageType.ResultRow);
        }
        var error = ProtocolErrorMessage.Decode((await peer.ExpectAsync(ProtocolMessageType.Error)).Payload.Span);
        serverSession.ExecuteResultOverride = null;
        await peer.SendAsync((ProtocolMessageType)SqlProtocolMessageType.Execute, ProtocolExecuteMessage.Create("SELECT 42").Encode());

        // Assert: no completion remained from the failed statement; this is the next header.
        error.Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);
        error.Message.ShouldStartWith(failure == "enumeration" ? "COHSQLE041:" : "COHSQLE045:", Case.Sensitive);
        if (failure is "oversized" or "binary")
        {
            error.Message.ShouldContain(failure == "binary" ? "16777219" : "16777220", Case.Sensitive);
            error.Message.ShouldContain(ProtocolFrameHeader.MaxPayloadLength.ToString(), Case.Sensitive);
        }
        result.Disposed.ShouldBeTrue();
        if (transaction is not null) { transaction.State.ShouldBe(TransactionState.Active); }
        result.RowsVisited.ShouldBe(failure == "header" ? 0 : failure is "enumeration" or "uncoded" or "dispose" ? 1 : 2);
        await peer.ExpectAsync((ProtocolMessageType)SqlProtocolMessageType.ResultHeader);
        await peer.ExpectAsync((ProtocolMessageType)SqlProtocolMessageType.ResultRow);
        await peer.ExpectAsync((ProtocolMessageType)SqlProtocolMessageType.ResultComplete);
        harness.Server.Sessions.ShouldHaveSingleItem().ShouldBeSameAs(serverSession);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql] - Result delivery: result failures roll back only the statement and keep an explicit transaction active")]
    [InlineData(false, "insert")]
    [InlineData(true, "insert")]
    [InlineData(false, "insert-select")]
    [InlineData(true, "insert-select")]
    [InlineData(false, "upsert")]
    [InlineData(true, "upsert")]
    [InlineData(false, "update")]
    [InlineData(true, "update")]
    [InlineData(false, "delete")]
    [InlineData(true, "delete")]
    public async Task QueryAsync_InjectedResultFailure_ShouldRollbackStatementAndKeepTransaction(bool explicitTransaction, string mutation)
    {
        // Arrange
        await using var harness = await ServerTestHarness.StartAsync();
        await using var client = SqlClient.Create(new()
        {
            Settings = new() { Database = ServerTestHarness.DatabaseName, EndPoint = harness.Listener.EndPoint, MaxPoolSize = 1 },
            ConnectionFactory = harness.Listener.CreateFactory(),
        });
        await using var connection = await client.ConnectAsync(TestTimeout.Token());
        var serverSession = (SqlDatabaseServerSession)harness.Server.Sessions.ShouldHaveSingleItem();
        await connection.ExecuteAsync("CREATE UNIQUE INDEX users_pk ON users(id)", cancellationToken: TestTimeout.Token());
        if (explicitTransaction)
        {
            await connection.ExecuteAsync("BEGIN", cancellationToken: TestTimeout.Token());
            await connection.ExecuteAsync("INSERT INTO users VALUES (3, 'pending')", cancellationToken: TestTimeout.Token());
        }
        var result = new ResultDeliveryFailureSet("value");
        serverSession.ExecuteResultOverride = () => result;
        string statement = mutation switch
        {
            "insert-select" => "INSERT INTO users SELECT 5, 'failed' RETURNING id",
            "upsert" => "INSERT INTO users VALUES (1, 'changed') ON CONFLICT(id) DO UPDATE SET name = EXCLUDED.name RETURNING id",
            "update" => "UPDATE users SET name = 'changed' RETURNING id",
            "delete" => "DELETE FROM users RETURNING id",
            _ => "INSERT INTO users VALUES (5, 'failed') RETURNING id",
        };

        // Act
        var failure = await Should.ThrowAsync<SqlClientException>(async () =>
            await connection.QueryAsync(statement, cancellationToken: TestTimeout.Token()));
        serverSession.ExecuteResultOverride = null;

        // Assert
        failure.Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);
        failure.Message.ShouldStartWith("COHSQLE045:", Case.Sensitive);
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHSQLE045");
        failure.ConnectionUsable.ShouldBeTrue();
        result.Disposed.ShouldBeTrue();
        if (explicitTransaction)
        {
            serverSession.DatabaseSession!.CurrentTransaction!.State.ShouldBe(TransactionState.Active);
        }
        (await connection.QueryAsync("SELECT id FROM users ORDER BY id", cancellationToken: TestTimeout.Token())).Count.ShouldBe(explicitTransaction ? 3 : 2);
        (await connection.QueryAsync("SELECT name FROM users WHERE id = 1", cancellationToken: TestTimeout.Token()))[0].GetString(0).ShouldBe("ada");
        if (!explicitTransaction) { await connection.ExecuteAsync("BEGIN", cancellationToken: TestTimeout.Token()); }
        await connection.ExecuteAsync("INSERT INTO users VALUES (4, 'later')", cancellationToken: TestTimeout.Token());
        await connection.ExecuteAsync("COMMIT", cancellationToken: TestTimeout.Token());
        (await connection.QueryAsync("SELECT id FROM users ORDER BY id", cancellationToken: TestTimeout.Token())).Count.ShouldBe(explicitTransaction ? 4 : 3);
        harness.Server.Sessions.ShouldHaveSingleItem().ShouldBeSameAs(serverSession);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql] - Result preparation: failures precede catalog and transaction commits")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_SelfCommittingResultFailure_ShouldKeepChangesUncommitted(bool commit)
    {
        // Arrange
        await using var harness = await ServerTestHarness.StartAsync();
        await using var peer = await harness.DialAsync();
        await peer.HandshakeAsync();
        var session = (SqlDatabaseServerSession)harness.Server.Sessions.ShouldHaveSingleItem();
        var transaction = commit ? await session.DatabaseSession!.BeginTransactionAsync(TestTimeout.Token()) : null;
        var result = new ResultDeliveryFailureSet("value");
        session.ExecuteResultOverride = () => result;

        // Act
        await peer.SendAsync((ProtocolMessageType)SqlProtocolMessageType.Execute,
            ProtocolExecuteMessage.Create(commit ? "COMMIT" : "CREATE TABLE failed(id INT)").Encode());
        var error = ProtocolErrorMessage.Decode((await peer.ExpectAsync(ProtocolMessageType.Error)).Payload.Span);
        session.ExecuteResultOverride = null;

        // Assert: no result frame preceded the error, and neither commit boundary was crossed.
        error.Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);
        error.Message.ShouldStartWith("COHSQLE045:", Case.Sensitive);
        result.Disposed.ShouldBeTrue();
        if (transaction is not null) { transaction.State.ShouldBe(TransactionState.Active); }
        await using var rows = (Assimalign.Cohesion.Database.Execution.DatabaseResultSet)await session.DatabaseSession!.ExecuteAsync(
            "SELECT table_name FROM information_schema.tables WHERE table_name = 'failed'", cancellationToken: TestTimeout.Token());
        int count = 0;
        await foreach (var row in rows.GetRowsAsync(TestTimeout.Token())) { count++; }
        count.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Result delivery: cancellation disposes the result and aborts the session transaction")]
    public async Task ExecuteAsync_CancelledEnumeration_ShouldDisposeResultAndEndTransaction()
    {
        // Arrange
        await using var harness = await ServerTestHarness.StartAsync();
        await using var peer = await harness.DialAsync();
        await peer.HandshakeAsync();
        var session = (SqlDatabaseServerSession)harness.Server.Sessions.ShouldHaveSingleItem();
        var transaction = await session.DatabaseSession!.BeginTransactionAsync(TestTimeout.Token());
        var result = new ResultDeliveryFailureSet("cancel");
        session.ExecuteResultOverride = () => result;

        // Act
        await peer.SendAsync((ProtocolMessageType)SqlProtocolMessageType.Execute, ProtocolExecuteMessage.Create("SELECT 1").Encode());
        await result.EnumerationBlocked.Task.WaitAsync(TestTimeout.Token());
        transaction.RunningStatements.ShouldBe(0);
        await session.DisposeAsync().AsTask().WaitAsync(TestTimeout.Token());

        // Assert
        result.Disposed.ShouldBeTrue();
        transaction.State.ShouldBe(TransactionState.RolledBack);
        harness.Server.Sessions.ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql] - Result preparation: deterministic failure and disposal orders release statement admission")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_DisposalDuringPreparation_ShouldReleaseStatementAndTransaction(bool failureFirst)
    {
        // Arrange: enumeration is held inside the INSERT apply bracket, before encoding fails.
        await using var harness = await ServerTestHarness.StartAsync();
        await using var peer = await harness.DialAsync();
        await peer.HandshakeAsync();
        var session = (SqlDatabaseServerSession)harness.Server.Sessions.ShouldHaveSingleItem();
        var transaction = await session.DatabaseSession!.BeginTransactionAsync(TestTimeout.Token());
        var result = new ResultDeliveryFailureSet("blocked");
        session.ExecuteResultOverride = () => result;

        // Act: the hook fixes each order instead of hoping a scheduler produces both.
        await peer.SendAsync((ProtocolMessageType)SqlProtocolMessageType.Execute,
            ProtocolExecuteMessage.Create("INSERT INTO users VALUES (5, 'failed') RETURNING id").Encode());
        await result.EnumerationBlocked.Task.WaitAsync(TestTimeout.Token());
        transaction.RunningStatements.ShouldBe(1);
        await Should.ThrowAsync<DatabaseException>(async () => await transaction.CommitAsync(TestTimeout.Token()));
        transaction.State.ShouldBe(TransactionState.Active);
        if (failureFirst)
        {
            result.ReleaseEnumeration.SetResult();
            var error = ProtocolErrorMessage.Decode((await peer.ExpectAsync(ProtocolMessageType.Error)).Payload.Span);
            error.Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);
            transaction.RunningStatements.ShouldBe(0);
            transaction.State.ShouldBe(TransactionState.Active);
        }
        await session.DisposeAsync().AsTask().WaitAsync(TestTimeout.Token());

        // Assert
        result.Disposed.ShouldBeTrue();
        transaction.RunningStatements.ShouldBe(0);
        transaction.State.ShouldBe(TransactionState.RolledBack);
        harness.Server.Sessions.ShouldBeEmpty();
        harness.Engine.TryGetDatabase(ServerTestHarness.DatabaseName, out var database).ShouldBeTrue();
        await using var fresh = await database!.CreateSessionAsync(TestTimeout.Token());
        await using var rows = (Assimalign.Cohesion.Database.Execution.DatabaseResultSet)await fresh.ExecuteAsync("SELECT id FROM users", cancellationToken: TestTimeout.Token());
        int count = 0;
        await foreach (var row in rows.GetRowsAsync(TestTimeout.Token())) { count++; }
        count.ShouldBe(2);
    }
}
