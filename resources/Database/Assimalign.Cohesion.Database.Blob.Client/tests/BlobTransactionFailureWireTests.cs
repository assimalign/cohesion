using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Database.Blob.Protocol;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Blob.Client.Tests;

/// <summary>
/// A failed operation inside an explicit transaction aborts it over the wire exactly as in process
/// (#1225). The Blob protocol has no transaction control, so each test opens the explicit
/// transaction on the server session's engine session, the scope every wire operation of that
/// connection executes in. A Blob wire failure is terminal: the server closes the connection and
/// its session, and the session's teardown ends the aborted transaction, so the host's ROLLBACK and
/// COMMIT must give the same answer whichever of the two runs first.
/// </summary>
public sealed class BlobTransactionFailureWireTests
{
    /// <summary>A failure, then a write, then ROLLBACK leaves the blobs unchanged.</summary>
    [Fact(DisplayName = "Cohesion Test [Blob.Client] - Transaction: a failed wire operation, a later write and ROLLBACK leave the blobs unchanged")]
    public async Task DeleteAsync_FailureInsideHostTransaction_ShouldAbortTransactionAndAcceptRollback()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = await BlobClientTestHarness.StartAsync(timeout.Token);
        await WriteAsync(harness.Container, "keep", "original", timeout.Token);
        var connection = await harness.Client.ConnectAsync(timeout.Token);
        var serverSession = harness.Server.Sessions.ShouldHaveSingleItem();
        var transaction = await serverSession.DatabaseSession.ShouldNotBeNull().BeginTransactionAsync(timeout.Token);
        (await connection.DeleteAsync("files", "keep", timeout.Token)).ShouldBeTrue();

        // Act
        var failure = await Should.ThrowAsync<BlobClientException>(async () =>
            await connection.DeleteAsync("missing", "item", timeout.Token));
        var refusedWrite = await Should.ThrowAsync<ObjectDisposedException>(async () =>
            await connection.DeleteAsync("files", "keep", timeout.Token));
        // The host's rollback may meet the session's teardown; both orders must succeed.
        await transaction.RollbackAsync(timeout.Token);
        await WaitUntilAsync(() => harness.Server.Sessions.Count == 0, timeout.Token);
        await transaction.RollbackAsync(timeout.Token);
        await connection.DisposeAsync();

        // Assert
        failure.Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);
        failure.Message.ShouldContain("Container 'missing' does not exist.", Case.Sensitive);
        refusedWrite.ObjectName.ShouldNotBeNullOrEmpty();
        transaction.State.ShouldBe(TransactionState.RolledBack);
        (await ReadAsync(harness.Container, "keep", timeout.Token)).ShouldBe("original");
        await using var next = await harness.Client.ConnectAsync(timeout.Token);
        (await next.GetPropertiesAsync("files", "keep", timeout.Token)).ShouldNotBeNull().Length.ShouldBe(8);
    }

    /// <summary>COMMIT of a transaction a wire operation aborted fails with CB04001 and commits nothing.</summary>
    [Fact(DisplayName = "Cohesion Test [Blob.Client] - Transaction: COMMIT after a failed wire operation fails and commits nothing")]
    public async Task CommitAsync_AfterFailedWireOperation_ShouldFailWithCodeAndCommitNothing()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = await BlobClientTestHarness.StartAsync(timeout.Token);
        await WriteAsync(harness.Container, "keep", "original", timeout.Token);
        await using var connection = await harness.Client.ConnectAsync(timeout.Token);
        var serverSession = harness.Server.Sessions.ShouldHaveSingleItem();
        var transaction = await serverSession.DatabaseSession.ShouldNotBeNull().BeginTransactionAsync(timeout.Token);
        (await connection.DeleteAsync("files", "keep", timeout.Token)).ShouldBeTrue();
        await Should.ThrowAsync<BlobClientException>(async () => await connection.DeleteAsync("missing", "item", timeout.Token));

        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await transaction.CommitAsync(timeout.Token));
        await WaitUntilAsync(() => harness.Server.Sessions.Count == 0, timeout.Token);
        var repeated = await Should.ThrowAsync<DatabaseException>(async () => await transaction.CommitAsync(timeout.Token));

        // Assert
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CB04001");
        error.Message.ShouldContain("Container 'missing' does not exist.", Case.Sensitive);
        repeated.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CB04001");
        transaction.State.ShouldBe(TransactionState.RolledBack);
        (await ReadAsync(harness.Container, "keep", timeout.Token)).ShouldBe("original");
    }

    [Theory(DisplayName = "Cohesion Test [Blob.Client] - A protocol failure before upload operation admission aborts earlier borrowed work with its exact cause (#1726)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UploadAsync_InvalidStartBeforeOperation_ShouldAbortHostWithProtocolCause(bool tcp)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        await using var harness = await BlobClientTestHarness.StartAsync(token, tcp);
        await WriteAsync(harness.Container, "keep", "original", token);
        await using var transport = await harness.Factory.ConnectAsync(harness.Listener.EndPoint, token);
        await using var channel = new ProtocolChannel(transport.AsStream(), BlobProtocol.Family, true);
        await SendAsync(ProtocolMessageType.Startup, new ProtocolStartupMessage(ProtocolVersion.Current, "app", "tester").Encode());
        (await ReadAsync()).Type.ShouldBe(ProtocolMessageType.Authenticate);
        await SendAsync(ProtocolMessageType.AuthenticateResponse, ReadOnlyMemory<byte>.Empty);
        channel.SetAgreement(ProtocolReadyMessage.Decode((await ReadAsync()).Payload.Span));
        var session = (BlobDatabaseSession)harness.Server.Sessions.Single().DatabaseSession!;
        await using var transaction = await session.BeginTransactionAsync(token);
        (await (await session.GetContainerAsync("files", token)).DeleteAsync("keep", token)).ShouldBeTrue();

        // Act: UploadAsync has not yet captured a transaction or opened a destination.
        await SendAsync((ProtocolMessageType)BlobProtocolMessageType.Write, new BlobWriteMessage("files", "other").Encode());
        await SendAsync((ProtocolMessageType)BlobProtocolMessageType.TransferComplete, new BlobTransferCompleteMessage(0).Encode());
        var failure = await ReadAsync();
        var commit = await Should.ThrowAsync<DatabaseException>(() => transaction.CommitAsync(token).AsTask());

        // Assert: teardown alone would retain its generic closure cause instead.
        failure.Type.ShouldBe(ProtocolMessageType.Error);
        ProtocolErrorMessage.Decode(failure.Payload.Span).Code.ShouldBe(ProtocolErrorCode.ProtocolViolation);
        commit.Diagnostics.ShouldContain(item => item.Code == "CB04001");
        commit.InnerException.ShouldBeOfType<ProtocolException>();
        (await BlobTransactionFailureWireTests.ReadAsync(harness.Container, "keep", token)).ShouldBe("original");
        (await harness.Container.GetPropertiesAsync("other", token)).ShouldBeNull();

        async Task SendAsync(ProtocolMessageType type, ReadOnlyMemory<byte> payload)
        {
            await channel.Writer.WriteFrameAsync(new(type, payload), token);
            await channel.Writer.FlushAsync(token);
        }
        async Task<ProtocolFrame> ReadAsync() => (await channel.Reader.ReadFrameAsync(token)).ShouldNotBeNull();
    }

    private static async Task WriteAsync(BlobContainer container, string name, string content, CancellationToken cancellationToken)
    {
        await using var stream = await container.OpenWriteAsync(name, cancellationToken: cancellationToken);
        await stream.WriteAsync(Encoding.UTF8.GetBytes(content), cancellationToken);
    }

    private static async Task<string> ReadAsync(BlobContainer container, string name, CancellationToken cancellationToken)
    {
        await using var stream = await container.OpenReadAsync(name, cancellationToken);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken);
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    // Session teardown is asynchronous with respect to the error frame the client read.
    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        while (!condition())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(10, CancellationToken.None);
        }
    }
}
