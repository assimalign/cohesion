using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.InMemory;
using Assimalign.Cohesion.Database.Client;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Graph.Client.Tests;

/// <summary>Malformed peers must never turn a failed graph exchange into a successful response or reusable lease.</summary>
public sealed class GraphClientFailureTests
{
    /// <summary>Failure after a delivered path is visible during enumeration and discards the connection.</summary>
    /// <param name="failure">The malformed terminal response.</param>
    [Theory(DisplayName = "Cohesion Test [Graph.Client] - Path errors truncation and wrong completion cannot become EOF")]
    [InlineData("error")]
    [InlineData("disconnect")]
    [InlineData("wrong-count")]
    [InlineData("unexpected-row")]
    public async Task QueryPathsAsync_FailureAfterPath_ShouldThrowAndDiscard(string failure)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var listener = new InMemoryConnectionListener();
        await using var client = CreateClient(listener);
        var pathReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task server = ServeAsync(listener, async channel =>
        {
            (await ReadAsync(channel, timeout.Token)).Type.ShouldBe((ProtocolMessageType)GraphProtocolMessageType.ExecutePaths);
            var path = new GraphProtocolPathMessage(
                [new GraphNode(new GraphNodeId(71), ["Person"], new Dictionary<string, object?> { ["name"] = "Alice" })], []);
            await WriteAsync(channel, (ProtocolMessageType)GraphProtocolMessageType.Path, path.Encode(), timeout.Token);
            await pathReceived.Task.WaitAsync(timeout.Token);
            if (failure == "error")
            {
                await WriteAsync(channel, ProtocolMessageType.Error,
                    new ProtocolErrorMessage(ProtocolErrorCode.ExecutionFailure, "Injected failure after a path.").Encode(), timeout.Token);
            }
            else if (failure == "wrong-count")
            {
                await WriteAsync(channel, (ProtocolMessageType)GraphProtocolMessageType.PathsComplete,
                    new GraphProtocolPathsCompleteMessage(2).Encode(), timeout.Token);
            }
            else if (failure == "unexpected-row")
            {
                await WriteAsync(channel, (ProtocolMessageType)GraphProtocolMessageType.ResultRow, ReadOnlyMemory<byte>.Empty, timeout.Token);
            }
            // Closing without PathsComplete is a truncated response.
        }, timeout.Token);
        await using var connection = await client.ConnectAsync(timeout.Token);
        await using var paths = connection.QueryPathsAsync("MATCH (n) RETURN n", cancellationToken: timeout.Token).GetAsyncEnumerator();
        (await paths.MoveNextAsync()).ShouldBeTrue();
        paths.Current.Nodes.ShouldHaveSingleItem().Id.Value.ShouldBe(71UL);

        // Act
        pathReceived.SetResult();
        var error = await Should.ThrowAsync<GraphClientException>(async () => await paths.MoveNextAsync());

        // Assert
        error.Code.ShouldBe(failure == "error" ? ProtocolErrorCode.ExecutionFailure : ProtocolErrorCode.ProtocolViolation);
        connection.IsOpen.ShouldBeFalse();
        await server.WaitAsync(timeout.Token);
    }

    /// <summary>A malformed initial path is rejected instead of exposing an invalid graph value.</summary>
    [Fact(DisplayName = "Cohesion Test [Graph.Client] - Malformed initial path invalidates the stream")]
    public async Task QueryPathsAsync_MalformedInitialPath_ShouldRejectAndDiscard()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var listener = new InMemoryConnectionListener();
        await using var client = CreateClient(listener);
        Task server = ServeAsync(listener, async channel =>
        {
            (await ReadAsync(channel, timeout.Token)).Type.ShouldBe((ProtocolMessageType)GraphProtocolMessageType.ExecutePaths);
            await WriteAsync(channel, (ProtocolMessageType)GraphProtocolMessageType.Path, new byte[] { 0, 0, 0, 0 }, timeout.Token);
        }, timeout.Token);
        await using var connection = await client.ConnectAsync(timeout.Token);
        await using var paths = connection.QueryPathsAsync("MATCH (n) RETURN n", cancellationToken: timeout.Token).GetAsyncEnumerator();

        // Act / Assert
        var error = await Should.ThrowAsync<GraphClientException>(async () => await paths.MoveNextAsync());
        error.Code.ShouldBe(ProtocolErrorCode.ProtocolViolation);
        connection.IsOpen.ShouldBeFalse();
        await server.WaitAsync(timeout.Token);
    }

    /// <summary>Even stable statement error codes cannot certify completion after a header or malformed row.</summary>
    /// <param name="failure">The response corruption.</param>
    [Theory(DisplayName = "Cohesion Test [Graph.Client] - Partial scalar responses never certify statement completion")]
    [InlineData("error-after-header")]
    [InlineData("short-row")]
    [InlineData("long-row")]
    [InlineData("row-without-header")]
    [InlineData("duplicate-header")]
    public async Task QueryAsync_PartialOrMalformedResponse_ShouldRejectAndDiscard(string failure)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var listener = new InMemoryConnectionListener();
        await using var client = CreateClient(listener);
        Task server = ServeAsync(listener, async channel =>
        {
            (await ReadAsync(channel, timeout.Token)).Type.ShouldBe((ProtocolMessageType)GraphProtocolMessageType.Execute);
            var header = new GraphProtocolResultHeaderMessage([("name", (byte)DatabaseType.String)]).Encode();
            if (failure != "row-without-header")
            {
                await WriteAsync(channel, (ProtocolMessageType)GraphProtocolMessageType.ResultHeader, header, timeout.Token);
            }
            if (failure == "error-after-header")
            {
                await WriteAsync(channel, ProtocolMessageType.Error,
                    new ProtocolErrorMessage(ProtocolErrorCode.ExecutionFailure, "Failure after result output.").Encode(), timeout.Token);
            }
            else if (failure == "duplicate-header")
            {
                await WriteAsync(channel, (ProtocolMessageType)GraphProtocolMessageType.ResultHeader, header, timeout.Token);
            }
            else
            {
                var row = new DatabaseKeyWriter();
                if (failure == "long-row")
                {
                    DatabaseValueCodec.Append(row, "Alice");
                    DatabaseValueCodec.Append(row, "unexpected");
                }
                await WriteAsync(channel, (ProtocolMessageType)GraphProtocolMessageType.ResultRow, row.ToArray(), timeout.Token);
            }
        }, timeout.Token);
        await using var connection = await client.ConnectAsync(timeout.Token);

        // Act / Assert
        var error = await Should.ThrowAsync<GraphClientException>(async () =>
            await connection.QueryAsync("MATCH (n) RETURN n.name", cancellationToken: timeout.Token));
        error.Code.ShouldBe(failure == "error-after-header" ? ProtocolErrorCode.ExecutionFailure : ProtocolErrorCode.ProtocolViolation);
        connection.IsOpen.ShouldBeFalse();
        await server.WaitAsync(timeout.Token);
    }

    private static IGraphClient CreateClient(InMemoryConnectionListener listener)
        => GraphClient.Create(new()
        {
            Settings = new DatabaseConnectionSettings { Database = "graph", EndPoint = listener.EndPoint, MaxPoolSize = 1 },
            ConnectionFactory = listener.CreateFactory(),
        });

    private static async Task ServeAsync(InMemoryConnectionListener listener, Func<ProtocolChannel, Task> exchange, CancellationToken token)
    {
        await using var transport = await listener.AcceptAsync(token);
        await using var channel = new ProtocolChannel(transport.AsStream(), GraphProtocol.Family);
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

    private static async ValueTask WriteAsync(ProtocolChannel channel, ProtocolMessageType type,
        ReadOnlyMemory<byte> payload, CancellationToken token)
    {
        await channel.Writer.WriteFrameAsync(new(type, payload), token);
        await channel.Writer.FlushAsync(token);
    }
}

