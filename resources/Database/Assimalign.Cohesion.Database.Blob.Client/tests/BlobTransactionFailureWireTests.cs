using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Transactions;
using Shouldly;
using Xunit;

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
        await Write(harness.Container, "keep", "original", timeout.Token);
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
        (await Read(harness.Container, "keep", timeout.Token)).ShouldBe("original");
        await using var next = await harness.Client.ConnectAsync(timeout.Token);
        (await next.GetPropertiesAsync("files", "keep", timeout.Token)).ShouldNotBeNull().Length.ShouldBe(8);
    }

    /// <summary>COMMIT of a transaction a wire operation aborted fails with COHDBB001 and commits nothing.</summary>
    [Fact(DisplayName = "Cohesion Test [Blob.Client] - Transaction: COMMIT after a failed wire operation fails and commits nothing")]
    public async Task CommitAsync_AfterFailedWireOperation_ShouldFailWithCodeAndCommitNothing()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = await BlobClientTestHarness.StartAsync(timeout.Token);
        await Write(harness.Container, "keep", "original", timeout.Token);
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
        error.Message.ShouldStartWith("COHDBB001", Case.Sensitive);
        error.Message.ShouldContain("Container 'missing' does not exist.", Case.Sensitive);
        repeated.Message.ShouldStartWith("COHDBB001", Case.Sensitive);
        transaction.State.ShouldBe(TransactionState.RolledBack);
        (await Read(harness.Container, "keep", timeout.Token)).ShouldBe("original");
    }

    /// <summary>
    /// A wire request refused before its operation starts still ends a host-opened transaction,
    /// because a Blob wire failure is terminal: the server aborts the transaction before it reports
    /// the failure, then closes the connection. The host's COMMIT fails with COHDBB001 naming the
    /// refusal before and after the teardown, its earlier wire write is undone, and a fresh
    /// connection works.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Blob.Client] - Transaction: a refused wire request ends the host transaction and COMMIT fails with COHDBB001")]
    public async Task UploadAsync_RefusedBeforeItStarts_ShouldEndHostTransactionAndFailCommitWithCode()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = await BlobClientTestHarness.StartAsync(timeout.Token);
        await Write(harness.Container, "keep", "original", timeout.Token);
        var connection = await harness.Client.ConnectAsync(timeout.Token);
        var serverSession = harness.Server.Sessions.ShouldHaveSingleItem();
        var transaction = await serverSession.DatabaseSession.ShouldNotBeNull().BeginTransactionAsync(timeout.Token);
        (await connection.DeleteAsync("files", "keep", timeout.Token)).ShouldBeTrue();

        // Act: an upload begins its own transaction, so BEGIN refuses it on a session that has one.
        var failure = await Should.ThrowAsync<BlobClientException>(async () =>
            await connection.UploadAsync("files", "other", new MemoryStream(new byte[10]), cancellationToken: timeout.Token));
        var commit = await Should.ThrowAsync<DatabaseException>(async () => await transaction.CommitAsync(timeout.Token));
        await WaitUntilAsync(() => harness.Server.Sessions.Count == 0, timeout.Token);
        var repeated = await Should.ThrowAsync<DatabaseException>(async () => await transaction.CommitAsync(timeout.Token));
        await transaction.RollbackAsync(timeout.Token);
        await connection.DisposeAsync();

        // Assert: the refusal is the root session base's one "already active" message
        // (concrete-types plan §6.4), for the model's former "A transaction or stream is already
        // active on this session.".
        failure.Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);
        failure.Message.ShouldContain("A transaction or operation is already active on this session.", Case.Sensitive);
        commit.Message.ShouldStartWith("COHDBB001", Case.Sensitive);
        commit.Message.ShouldContain("nothing was committed", Case.Sensitive);
        commit.Message.ShouldContain(failure.Message, Case.Sensitive);
        repeated.Message.ShouldBe(commit.Message);
        transaction.State.ShouldBe(TransactionState.RolledBack);
        (await Read(harness.Container, "keep", timeout.Token)).ShouldBe("original");
        await using var next = await harness.Client.ConnectAsync(timeout.Token);
        (await next.GetPropertiesAsync("files", "other", timeout.Token)).ShouldBeNull();
    }

    private static async Task Write(BlobContainer container, string name, string content, CancellationToken cancellationToken)
    {
        await using var stream = await container.OpenWriteAsync(name, cancellationToken: cancellationToken);
        await stream.WriteAsync(Encoding.UTF8.GetBytes(content), cancellationToken);
    }

    private static async Task<string> Read(BlobContainer container, string name, CancellationToken cancellationToken)
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
