using System.Collections.Generic;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.KeyValuePair.Internal;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.KeyValuePair.Tests;

using static KeyValueTestHarness;

/// <summary>
/// The #1225 audit over the key-value wire. The protocol has no transaction control, so each test
/// opens the explicit transaction on the server session's engine session, the scope every wire
/// command of that connection executes in. A failed wire command writes nothing and keeps both the
/// session and the transaction, so later commands stay inside the transaction; a connection that
/// ends rolls the transaction back, and the host's own rollback afterwards raises nothing.
/// </summary>
public sealed class KeyValueTransactionFailureWireTests
{
    /// <summary>A rejected command, then a write, then ROLLBACK leaves the key space unchanged.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Wire transaction: a failed command, a later write and ROLLBACK leave the key space unchanged")]
    public async Task Execute_FailureThenWriteThenRollback_ShouldLeaveKeySpaceUnchanged()
    {
        // Arrange
        await using var harness = await KeyValueServerHarness.StartAsync();
        await using var client = await harness.DialAsync();
        await client.HandshakeAsync();
        var serverSession = harness.Server.Context.Sessions.ShouldHaveSingleItem();
        var transaction = await serverSession.DatabaseSession.ShouldNotBeNull().BeginTransactionAsync(TestTimeout.Token());
        await PutAsync(client, "pending");

        // Act
        await client.SendAsync(ProtocolMessageType.Execute, Command("PUT @k", ("k", Bytes("broken"))).Encode());
        var failure = ProtocolErrorMessage.Decode((await client.ExpectAsync(ProtocolMessageType.Error)).Payload.Span);
        var stateAfterFailure = transaction.State;
        await PutAsync(client, "later");
        await transaction.RollbackAsync(TestTimeout.Token());

        // Assert: the session is back in autocommit, and a scan returns no rows.
        failure.Code.ShouldBe(ProtocolErrorCode.ParseFailure);
        stateAfterFailure.ShouldBe(TransactionState.Active);
        transaction.State.ShouldBe(TransactionState.RolledBack);
        harness.Server.Context.Sessions.ShouldHaveSingleItem().Id.ShouldBe(serverSession.Id);
        serverSession.DatabaseSession!.CurrentTransaction.ShouldBeNull();
        await client.SendAsync(ProtocolMessageType.Execute, Command("SCAN").Encode());
        await client.ExpectAsync(ProtocolMessageType.ResultHeader);
        await client.ExpectAsync(ProtocolMessageType.ResultComplete);
    }

    /// <summary>A connection that ends rolls its transaction back, and the host's rollback afterwards is a no-op.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Wire transaction: a closed connection ends the transaction and a host rollback raises nothing")]
    public async Task Terminate_InsideHostTransaction_ShouldEndTransactionAndAcceptRollback()
    {
        // Arrange
        await using var harness = await KeyValueServerHarness.StartAsync();
        var client = await harness.DialAsync();
        await client.HandshakeAsync();
        var serverSession = harness.Server.Context.Sessions.ShouldHaveSingleItem();
        var transaction = await serverSession.DatabaseSession.ShouldNotBeNull().BeginTransactionAsync(TestTimeout.Token());
        await PutAsync(client, "pending");

        // Act: the host's rollback may meet the session's teardown; both orders must succeed.
        await client.SendAsync(ProtocolMessageType.Terminate);
        await client.DisposeAsync();
        await transaction.RollbackAsync(TestTimeout.Token());
        await KeyValueServerHarness.WaitUntilAsync(() => harness.Server.Context.Sessions.Count == 0);
        await transaction.RollbackAsync(TestTimeout.Token());

        // Assert
        transaction.State.ShouldBe(TransactionState.RolledBack);
        harness.Engine.TryGetDatabase(KeyValueServerHarness.DatabaseName, out var opened).ShouldBeTrue();
        var database = (IKeyValueDatabase)opened;
        await using var observer = await database.CreateSessionAsync();
        (await database.GetAsync(observer, Bytes("pending"), TestTimeout.Token())).ShouldBeNull();
    }

    /// <summary>
    /// A host rollback while a wire command of its transaction waits for a key lock fails that
    /// command with ExecutionFailure: nothing of it is written, the session stays ready, and the
    /// key's next writer is not blocked (#1225 review).
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Wire transaction: a host rollback fails a wire command parked on a key lock and writes nothing")]
    public async Task Execute_HostRollsBackWhileCommandWaitsForKeyLock_ShouldFailCommandAndWriteNothing()
    {
        // Arrange: another transaction holds the lock of a fresh key the wire command will write.
        await using var harness = await KeyValueServerHarness.StartAsync();
        await using var client = await harness.DialAsync();
        await client.HandshakeAsync();
        var serverSession = harness.Server.Context.Sessions.ShouldHaveSingleItem();
        harness.Engine.TryGetDatabase(KeyValueServerHarness.DatabaseName, out var opened).ShouldBeTrue();
        var database = (IKeyValueDatabase)opened;
        await using var blockingSession = await database.CreateSessionAsync();
        await using var observer = await database.CreateSessionAsync();
        var blocker = await blockingSession.BeginTransactionAsync(TestTimeout.Token());
        await database.PutAsync(blockingSession, Bytes("fresh"), Bytes("blocker"), cancellationToken: TestTimeout.Token());
        var transaction = await serverSession.DatabaseSession.ShouldNotBeNull().BeginTransactionAsync(TestTimeout.Token());
        await client.SendAsync(ProtocolMessageType.Execute, Command("PUT @k @v", ("k", Bytes("fresh")), ("v", Bytes("wire"))).Encode());

        // Once the command is admitted into the transaction, it can only run inside it.
        await KeyValueServerHarness.WaitUntilAsync(() => ((KeyValueDatabaseTransaction)transaction).RunningCommands == 1);

        // Act: the host ends its transaction under the command, then the blocker rolls back, which
        // would grant the key to a command that had queued just after the host's rollback.
        await transaction.RollbackAsync(TestTimeout.Token());
        await blocker.RollbackAsync(TestTimeout.Token());
        var failure = ProtocolErrorMessage.Decode((await client.ExpectAsync(ProtocolMessageType.Error)).Payload.Span);
        var fresh = await database.GetAsync(observer, Bytes("fresh"), TestTimeout.Token());
        var later = await database.PutAsync(observer, Bytes("fresh"), Bytes("later"), cancellationToken: TestTimeout.Token(5));

        // Assert
        failure.Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);
        transaction.State.ShouldBe(TransactionState.RolledBack);
        fresh.ShouldBeNull();
        later.Applied.ShouldBeTrue();
        harness.Server.Context.Sessions.ShouldHaveSingleItem().Id.ShouldBe(serverSession.Id);
        await PutAsync(client, "after");
    }

    private static async Task PutAsync(KeyValueProtocolClient client, string key)
    {
        await client.SendAsync(ProtocolMessageType.Execute, Command("PUT @k @v", ("k", Bytes(key)), ("v", Bytes(key))).Encode());
        await client.ExpectAsync(ProtocolMessageType.ResultHeader);
        await client.ExpectAsync(ProtocolMessageType.ResultRow);
        await client.ExpectAsync(ProtocolMessageType.ResultComplete);
    }

    private static ProtocolExecuteMessage Command(string text, params (string Name, object? Value)[] parameters)
    {
        var encoded = new Dictionary<string, byte[]>(parameters.Length);
        foreach (var (name, value) in parameters)
        {
            encoded[name] = DatabaseValueCodec.EncodeComponent(value);
        }
        return new ProtocolExecuteMessage(text, encoded);
    }
}
