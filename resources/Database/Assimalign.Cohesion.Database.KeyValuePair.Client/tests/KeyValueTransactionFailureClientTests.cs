using System;
using System.Text;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.KeyValuePair.Client.Tests;

/// <summary>
/// The #1225 audit through the typed client. The key-value protocol has no transaction control, so
/// the test opens the explicit transaction on the server session's engine session. A command that
/// fails with a retryable conflict writes nothing and leaves the transaction active, so later
/// commands stay inside it, and the host's ROLLBACK undoes every one of them.
/// </summary>
public sealed class KeyValueTransactionFailureClientTests
{
    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    private static string Text(ReadOnlyMemory<byte> bytes) => Encoding.UTF8.GetString(bytes.Span);

    /// <summary>
    /// A conflict inside the host's transaction fails one client command and leaves the transaction
    /// active; the client's later write stays inside it, and the host's ROLLBACK undoes every write.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair.Client] - Transaction: a failed command, a later write and ROLLBACK leave the key space unchanged")]
    public async Task PutAsync_ConflictInsideHostTransaction_ShouldKeepTransactionAndRollBackEveryWrite()
    {
        // Arrange
        await using var harness = await KeyValueClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(KeyValueClientTestHarness.Timeout());
        await connection.PutAsync(Bytes("keep"), Bytes("original"), KeyValueClientTestHarness.Timeout());
        var serverSession = harness.Server.Sessions.ShouldHaveSingleItem();
        var transaction = await serverSession.DatabaseSession.ShouldNotBeNull().BeginTransactionAsync(KeyValueClientTestHarness.Timeout());
        harness.Engine.TryGetDatabase(KeyValueClientTestHarness.DatabaseName, out var database).ShouldBeTrue();
        await using (var other = await database.CreateSessionAsync())
        {
            // Another session replaces the key after the host's snapshot began: first-updater-wins.
            await database.PutAsync(other, Bytes("keep"), Bytes("changed"), cancellationToken: KeyValueClientTestHarness.Timeout());
        }
        await connection.PutAsync(Bytes("pending"), Bytes("pending"), KeyValueClientTestHarness.Timeout());

        // Act
        var failure = await Should.ThrowAsync<KeyValueClientException>(async () =>
            await connection.PutAsync(Bytes("keep"), Bytes("mine"), KeyValueClientTestHarness.Timeout()));
        var stateAfterFailure = transaction.State;
        await connection.PutAsync(Bytes("later"), Bytes("later"), KeyValueClientTestHarness.Timeout());
        await using var observer = await database.CreateSessionAsync();
        var outside = await database.GetAsync(observer, Bytes("later"), KeyValueClientTestHarness.Timeout());
        await transaction.RollbackAsync(KeyValueClientTestHarness.Timeout());

        // Assert
        failure.Message.ShouldContain("first-updater-wins", Case.Sensitive);
        connection.IsOpen.ShouldBeTrue();
        stateAfterFailure.ShouldBe(TransactionState.Active);
        outside.ShouldBeNull();
        transaction.State.ShouldBe(TransactionState.RolledBack);
        Text((await connection.GetAsync(Bytes("keep"), KeyValueClientTestHarness.Timeout()))!.Value.Value).ShouldBe("changed");
        (await connection.ExistsAsync(Bytes("pending"), KeyValueClientTestHarness.Timeout())).ShouldBeFalse();
        (await connection.ExistsAsync(Bytes("later"), KeyValueClientTestHarness.Timeout())).ShouldBeFalse();
    }
}
