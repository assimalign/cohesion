using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Database.Blob.Protocol;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Blob.Client.Tests;

[Collection("Savepoint adapter wire failures")]
public sealed class BorrowedTransferWireTests
{
    [Theory(DisplayName = "Cohesion Test [Blob.Client] - A host end between lookup and upload admission never becomes autocommit (#1726)")]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task UploadAsync_StaleHost_ShouldRefuseBeforeWriting(bool tcp, bool rollback)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        await using var harness = await BlobClientTestHarness.StartAsync(token, tcp);
        await WriteAsync(harness.Container, "one", 20_000, token);
        await using var transport = await harness.Factory.ConnectAsync(harness.Listener.EndPoint, token);
        await using var channel = new ProtocolChannel(transport.AsStream(), BlobProtocol.Family, true);
        await StartAsync(channel, token);
        var server = harness.Server.Sessions.Single();
        var session = (BlobDatabaseSession)server.DatabaseSession!;
        await using var transaction = await session.BeginTransactionAsync(token);
        int crossings = 0;
        BeforeUploadOpen(server, () =>
        {
            if (rollback) { transaction.RollbackAsync(token).AsTask().GetAwaiter().GetResult(); }
            else { transaction.CommitAsync(token).AsTask().GetAwaiter().GetResult(); }
            session.CurrentTransaction.ShouldBeNull();
            crossings++;
        });

        // Act: pipelined transfer frames cannot bypass admission of the captured host.
        await SendAsync(channel, BlobProtocolMessageType.Write, new BlobWriteMessage("files", "one").Encode(), token);
        await SendAsync(channel, BlobProtocolMessageType.TransferStart, new BlobTransferStartMessage(-1, "").Encode(), token);
        await channel.Writer.WriteFrameAsync(new BlobChunkMessage(new byte[100]).ToFrame(), token);
        await channel.Writer.FlushAsync(token);
        await SendAsync(channel, BlobProtocolMessageType.TransferComplete, new BlobTransferCompleteMessage(101).Encode(), token);
        var refusal = await ReadAsync(channel, token);

        // Assert
        crossings.ShouldBe(1);
        refusal.Type.ShouldBe(ProtocolMessageType.Error);
        ProtocolErrorMessage.Decode(refusal.Payload.Span).Message.ShouldStartWith("CK04005:", Case.Sensitive);
        await AssertContentAsync(harness.Container, "one", 20_000, token);
    }

    [Theory(DisplayName = "Cohesion Test [Blob.Client] - Wrong-length upload completion aborts its operation while the connection is still live (#1726)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UploadAsync_WrongLength_ShouldAbortBeforeDisposalAndTeardown(bool tcp)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        await using var harness = await BlobClientTestHarness.StartAsync(token, tcp);
        await WriteAsync(harness.Container, "one", 20_000, token);
        await using var transport = await harness.Factory.ConnectAsync(harness.Listener.EndPoint, token);
        await using var channel = new ProtocolChannel(transport.AsStream(), BlobProtocol.Family, true);
        await StartAsync(channel, token);
        var server = harness.Server.Sessions.Single();
        var session = (BlobDatabaseSession)server.DatabaseSession!;
        await using var transaction = await session.BeginTransactionAsync(token);
        await SendAsync(channel, BlobProtocolMessageType.Write, new BlobWriteMessage("files", "one").Encode(), token);
        await SendAsync(channel, BlobProtocolMessageType.TransferStart, new BlobTransferStartMessage(-1, "").Encode(), token);
        await channel.Writer.WriteFrameAsync(new BlobChunkMessage(new byte[100]).ToFrame(), token);
        await channel.Writer.FlushAsync(token);
        (await ReadAsync(channel, token)).Type.ShouldBe((ProtocolMessageType)BlobProtocolMessageType.ChunkAcknowledgement);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        int abortedWhileLive = 0;
        PinRelease(harness.Database, () => { entered.TrySetResult(); release.Wait(token); });

        // Act / Assert: this is the operation's finish, before the pump can write an error or close.
        try
        {
            await SendAsync(channel, BlobProtocolMessageType.TransferComplete, new BlobTransferCompleteMessage(101).Encode(), token);
            await entered.Task.WaitAsync(token);
            Lifetime(server).IsCancellationRequested.ShouldBeFalse();
            session.State.ShouldBe(SessionState.Open);
            transaction.State.ShouldBe(TransactionState.Faulted);
            Context(transaction).State.ShouldNotBe(TransactionState.Active);
            abortedWhileLive++;
        }
        finally { PinRelease(harness.Database, null); release.Set(); }
        abortedWhileLive.ShouldBe(1);
        var error = await ReadAsync(channel, token);
        ProtocolErrorMessage.Decode(error.Payload.Span).Code.ShouldBe(ProtocolErrorCode.ProtocolViolation);
        var commit = await Should.ThrowAsync<DatabaseException>(() => transaction.CommitAsync(token).AsTask());
        commit.InnerException.ShouldBeOfType<ProtocolException>();
        await AssertContentAsync(harness.Container, "one", 20_000, token);
    }

    [Theory(DisplayName = "Cohesion Test [Blob.Client] - Borrowed download releases its hold before completion and an immediate host commit (#1726)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DownloadAsync_CompletedBorrow_ShouldPermitImmediateCommit(bool tcp)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        await using var harness = await BlobClientTestHarness.StartAsync(token, tcp);
        await WriteAsync(harness.Container, "one", 20_000, token);
        await using var connection = await harness.Client.ConnectAsync(token);
        var server = harness.Server.Sessions.Single();
        var session = (BlobDatabaseSession)server.DatabaseSession!;
        await using var transaction = await session.BeginTransactionAsync(token);
        int released = 0;
        DownloadReleased(server, () =>
        {
            Operations(transaction).ShouldBe(0);
            Interlocked.Increment(ref released);
        });

        // Act
        await using var download = await connection.DownloadAsync("files", "one", token);
        await download.CopyToAsync(Stream.Null, token);
        await transaction.CommitAsync(token);

        // Assert: the server recorded the released hold before TransferComplete was emitted.
        released.ShouldBe(1);
        transaction.State.ShouldBe(TransactionState.Committed);
        session.CurrentTransaction.ShouldBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Blob.Client] - Concurrent replacement keeps READ COMMITTED download metadata and content on one version (#1726)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DownloadAsync_ConcurrentReplacement_ShouldUseOneVersion(bool tcp)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        await using var harness = await BlobClientTestHarness.StartAsync(token, tcp);
        await WriteAsync(harness.Container, "one", 20_000, token);
        await using var transport = await harness.Factory.ConnectAsync(harness.Listener.EndPoint, token);
        await using var channel = new ProtocolChannel(transport.AsStream(), BlobProtocol.Family, true);
        await StartAsync(channel, token);
        var server = harness.Server.Sessions.Single();
        var session = (BlobDatabaseSession)server.DatabaseSession!;
        await using var transaction = await session.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
        int replacements = 0;
        DownloadOpened(server, () =>
        {
            Operations(transaction).ShouldBe(1);
            WriteAsync(harness.Container, "one", 30_000, token).GetAwaiter().GetResult();
            replacements++;
        });

        // Act
        await SendAsync(channel, BlobProtocolMessageType.Read, new BlobReadMessage("files", "one").Encode(), token);
        using var content = new MemoryStream();
        var metadata = await BlobProtocolTransfer.ReceiveAsync(channel, content, token);

        // Assert
        replacements.ShouldBe(1);
        metadata.Length.ShouldBe(20_000);
        content.ToArray().ShouldBe(new byte[20_000]);
        await transaction.CommitAsync(token);
        await AssertContentAsync(harness.Container, "one", 30_000, token);
    }

    [Theory(DisplayName = "Cohesion Test [Blob.Client] - Invalid download acknowledgement aborts the borrowed operation before releasing its hold (#1726)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DownloadAsync_InvalidAcknowledgement_ShouldAbortBorrowedWork(bool tcp)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        await using var harness = await BlobClientTestHarness.StartAsync(token, tcp);
        await WriteAsync(harness.Container, "one", 20_000, token);
        await WriteAsync(harness.Container, "keep", 10, token);
        await using var transport = await harness.Factory.ConnectAsync(harness.Listener.EndPoint, token);
        await using var channel = new ProtocolChannel(transport.AsStream(), BlobProtocol.Family, true);
        await StartAsync(channel, token);
        var server = harness.Server.Sessions.Single();
        var session = (BlobDatabaseSession)server.DatabaseSession!;
        await using var transaction = await session.BeginTransactionAsync(token);
        (await (await session.GetContainerAsync("files", token)).DeleteAsync("keep", token)).ShouldBeTrue();
        await SendAsync(channel, BlobProtocolMessageType.Read, new BlobReadMessage("files", "one").Encode(), token);
        (await ReadAsync(channel, token)).Type.ShouldBe((ProtocolMessageType)BlobProtocolMessageType.TransferStart);
        (await ReadAsync(channel, token)).Type.ShouldBe((ProtocolMessageType)BlobProtocolMessageType.Chunk);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        PinRelease(harness.Database, () => { entered.TrySetResult(); release.Wait(token); });

        // Act / Assert
        try
        {
            await SendAsync(channel, BlobProtocolMessageType.ChunkAcknowledgement, new BlobChunkAcknowledgementMessage(0).Encode(), token);
            await entered.Task.WaitAsync(token);
            Lifetime(server).IsCancellationRequested.ShouldBeFalse();
            transaction.State.ShouldBe(TransactionState.Faulted);
            Context(transaction).State.ShouldNotBe(TransactionState.Active);
        }
        finally { PinRelease(harness.Database, null); release.Set(); }
        ProtocolErrorMessage.Decode((await ReadAsync(channel, token)).Payload.Span).Code.ShouldBe(ProtocolErrorCode.ProtocolViolation);
        var commit = await Should.ThrowAsync<DatabaseException>(() => transaction.CommitAsync(token).AsTask());
        commit.InnerException.ShouldBeOfType<ProtocolException>();
        await AssertContentAsync(harness.Container, "keep", 10, token);
    }

    private static async Task StartAsync(ProtocolChannel channel, CancellationToken token)
    {
        await channel.Writer.WriteFrameAsync(new(ProtocolMessageType.Startup,
            new ProtocolStartupMessage(ProtocolVersion.Current, "app", "tester").Encode()), token);
        await channel.Writer.FlushAsync(token);
        (await ReadAsync(channel, token)).Type.ShouldBe(ProtocolMessageType.Authenticate);
        await channel.Writer.WriteFrameAsync(new(ProtocolMessageType.AuthenticateResponse, ReadOnlyMemory<byte>.Empty), token);
        await channel.Writer.FlushAsync(token);
        channel.SetAgreement(ProtocolReadyMessage.Decode((await ReadAsync(channel, token)).Payload.Span));
    }

    private static async Task SendAsync(ProtocolChannel channel, BlobProtocolMessageType type, ReadOnlyMemory<byte> payload, CancellationToken token)
    {
        await channel.Writer.WriteFrameAsync(new((ProtocolMessageType)type, payload), token);
        await channel.Writer.FlushAsync(token);
    }

    private static async Task<ProtocolFrame> ReadAsync(ProtocolChannel channel, CancellationToken token)
        => (await channel.Reader.ReadFrameAsync(token)).ShouldNotBeNull();

    private static async Task WriteAsync(BlobContainer container, string name, int length, CancellationToken token)
    {
        await using var output = await container.OpenWriteAsync(name, cancellationToken: token);
        await output.WriteAsync(new byte[length], token);
    }

    private static async Task AssertContentAsync(BlobContainer container, string name, int length, CancellationToken token)
    {
        await using var input = await container.OpenReadAsync(name, token);
        using var content = new MemoryStream();
        await input.CopyToAsync(content, token);
        content.ToArray().ShouldBe(new byte[length]);
    }

    private const string serverType = "Assimalign.Cohesion.Database.Blob.Internal.BlobDatabaseServerSession, Assimalign.Cohesion.Database.Blob";
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_BeforeUploadOpen")]
    private static extern void BeforeUploadOpen([UnsafeAccessorType(serverType)] object server, Action? callback);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_DownloadOpened")]
    private static extern void DownloadOpened([UnsafeAccessorType(serverType)] object server, Action? callback);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_DownloadReleased")]
    private static extern void DownloadReleased([UnsafeAccessorType(serverType)] object server, Action? callback);
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_lifetimeSource")]
    private static extern ref CancellationTokenSource Lifetime([UnsafeAccessorType(serverType)] object server);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_BeforeSnapshotPinRelease")]
    private static extern void PinRelease(BlobDatabase database, Action? callback);
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_operations")]
    private static extern ref int Operations(DatabaseTransaction transaction);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Context")]
    private static extern TransactionContext Context(BlobDatabaseTransaction transaction);
}
