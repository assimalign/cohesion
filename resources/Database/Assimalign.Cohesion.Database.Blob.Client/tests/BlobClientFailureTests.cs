using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.InMemory;
using Assimalign.Cohesion.Database.Client;
using Assimalign.Cohesion.Database.Protocol;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Blob.Client.Tests;

/// <summary>Peer failures after content has crossed the wire must remain visible to callers.</summary>
public sealed class BlobClientFailureTests
{
    [Fact(DisplayName = "Cohesion Test [Blob.Client] - Upload error: A server failure after an acknowledged chunk throws clearly")]
    public async Task UploadAsync_ServerErrorAfterAcceptedChunk_ShouldExposeFailureAndStopSource()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var listener = new InMemoryConnectionListener();
        await using var client = CreateClient(listener);
        Task server = ServeAsync(listener, async channel =>
        {
            (await ReadAsync(channel, timeout.Token)).Type.ShouldBe((ProtocolMessageType)BlobProtocolMessageType.Write);
            (await ReadAsync(channel, timeout.Token)).Type.ShouldBe((ProtocolMessageType)BlobProtocolMessageType.TransferStart);
            var first = await ReadAsync(channel, timeout.Token);
            first.Type.ShouldBe((ProtocolMessageType)BlobProtocolMessageType.Chunk);
            await WriteAsync(channel, (ProtocolMessageType)BlobProtocolMessageType.ChunkAcknowledgement,
                new BlobChunkAcknowledgementMessage(first.Payload.Length).Encode(), timeout.Token);
            (await ReadAsync(channel, timeout.Token)).Type.ShouldBe((ProtocolMessageType)BlobProtocolMessageType.Chunk);
            await WriteAsync(channel, ProtocolMessageType.Error,
                new ProtocolErrorMessage(ProtocolErrorCode.ExecutionFailure, "Injected storage write failure after accepted chunk.").Encode(), timeout.Token);
        }, timeout.Token);
        await using var connection = await client.ConnectAsync(timeout.Token);
        using var source = new GeneratedContentStream(10L * BlobProtocol.MaxChunkLength);

        var exception = await Should.ThrowAsync<BlobClientException>(async () =>
            await connection.UploadAsync("files", "target", source, cancellationToken: timeout.Token));

        exception.Message.ShouldContain("Injected storage write failure", Case.Sensitive);
        exception.Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);
        source.BytesRead.ShouldBe(2L * BlobProtocol.MaxChunkLength);
        await server.WaitAsync(timeout.Token);
    }

    [Theory(DisplayName = "Cohesion Test [Blob.Client] - Download failure: Error, truncation and false completion never look like EOF")]
    [InlineData("error")]
    [InlineData("disconnect")]
    [InlineData("wrong-length")]
    public async Task DownloadAsync_PeerFailsAfterContent_ShouldThrowAndKeepFailureSticky(string failure)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var listener = new InMemoryConnectionListener();
        await using var client = CreateClient(listener);
        var firstRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task server = ServeAsync(listener, async channel =>
        {
            (await ReadAsync(channel, timeout.Token)).Type.ShouldBe((ProtocolMessageType)BlobProtocolMessageType.Read);
            await WriteAsync(channel, (ProtocolMessageType)BlobProtocolMessageType.TransferStart,
                new BlobTransferStartMessage(2L * BlobProtocol.MaxChunkLength).Encode(), timeout.Token);
            await WriteAsync(channel, (ProtocolMessageType)BlobProtocolMessageType.Chunk,
                new byte[BlobProtocol.MaxChunkLength], timeout.Token);
            await firstRead.Task.WaitAsync(timeout.Token);
            if (failure == "error")
            {
                await WriteAsync(channel, ProtocolMessageType.Error,
                    new ProtocolErrorMessage(ProtocolErrorCode.ExecutionFailure, "Injected storage read failure after content.").Encode(), timeout.Token);
            }
            else if (failure == "wrong-length")
            {
                await WriteAsync(channel, (ProtocolMessageType)BlobProtocolMessageType.TransferComplete,
                    new BlobTransferCompleteMessage(2L * BlobProtocol.MaxChunkLength).Encode(), timeout.Token);
            }
            // Returning closes the peer, with no completion in the disconnect case.
        }, timeout.Token);
        await using var connection = await client.ConnectAsync(timeout.Token);
        await using var download = await connection.DownloadAsync("files", "target", timeout.Token);
        var buffer = new byte[BlobProtocol.MaxChunkLength];
        (await download.ReadAsync(buffer, timeout.Token)).ShouldBeGreaterThan(0);
        firstRead.TrySetResult();

        var exception = await Should.ThrowAsync<BlobClientException>(async () =>
        {
            while (await download.ReadAsync(buffer, timeout.Token) != 0) { }
        });

        if (failure == "error")
        {
            exception.Message.ShouldContain("Injected storage read failure", Case.Sensitive);
            exception.Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);
        }
        await Should.ThrowAsync<BlobClientException>(async () => { _ = await download.ReadAsync(buffer, timeout.Token); });
        await server.WaitAsync(timeout.Token);
    }

    private static IBlobClient CreateClient(InMemoryConnectionListener listener)
        => BlobClient.Create(new BlobClientOptions
        {
            Settings = new DatabaseConnectionSettings { Database = "app", Principal = "tester", EndPoint = listener.EndPoint },
            ConnectionFactory = listener.CreateFactory()
        });

    private static async Task ServeAsync(InMemoryConnectionListener listener, Func<ProtocolChannel, Task> exchange, CancellationToken token)
    {
        await using var transport = await listener.AcceptAsync(token);
        await using var channel = new ProtocolChannel(transport.AsStream(), BlobProtocol.Family);
        (await ReadAsync(channel, token)).Type.ShouldBe(ProtocolMessageType.Startup);
        await WriteAsync(channel, ProtocolMessageType.Authenticate, ReadOnlyMemory<byte>.Empty, token);
        (await ReadAsync(channel, token)).Type.ShouldBe(ProtocolMessageType.AuthenticateResponse);
        await WriteAsync(channel, ProtocolMessageType.Ready, ReadOnlyMemory<byte>.Empty, token);
        await exchange(channel);
    }

    private static async ValueTask<ProtocolFrame> ReadAsync(ProtocolChannel channel, CancellationToken token)
    {
        var frame = await channel.Reader.ReadFrameAsync(token);
        frame.ShouldNotBeNull();
        return frame.Value;
    }

    private static async ValueTask WriteAsync(ProtocolChannel channel, ProtocolMessageType type, ReadOnlyMemory<byte> payload, CancellationToken token)
    {
        await channel.Writer.WriteFrameAsync(new(type, payload), token);
        await channel.Writer.FlushAsync(token);
    }
}
