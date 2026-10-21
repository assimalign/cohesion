using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using Xunit;
using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.InMemory;
using Assimalign.Cohesion.Database.Client;
using Assimalign.Cohesion.Database.Graph.Client.Internal;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Types;

using Assimalign.Cohesion.Database.Graph.Protocol;

namespace Assimalign.Cohesion.Database.Graph.Client.Tests;

/// <summary>Malformed peers must never turn a failed graph exchange into a successful response or reusable lease.</summary>
public sealed class GraphClientFailureTests
{
    [Fact(DisplayName = "Cohesion Test [Graph.Client] - A terminal diagnostic received before cancellation retains its original failure")]
    public async Task ReadPathAsync_TerminalFailureBeforeCancellation_ShouldPreserveDiagnostic()
    {
        // Arrange: finish the producer first, then cancel the handoff read deterministically.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var listener = new InMemoryConnectionListener();
        await using var client = DatabaseClient.Create(new()
        {
            Family = GraphProtocol.Family,
            Settings = new DatabaseConnectionSettings { Database = "graph", EndPoint = listener.EndPoint, MaxPoolSize = 1 },
            ConnectionFactory = listener.CreateFactory(),
        });
        Task server = ServeAsync(listener, async channel =>
        {
            (await ReadAsync(channel, timeout.Token)).Type.ShouldBe((ProtocolMessageType)GraphProtocolMessageType.ExecutePaths);
            await WriteAsync(channel, ProtocolMessageType.Diagnostics, new ProtocolDiagnosticsMessage(
                [new ProtocolDiagnostic(ProtocolDiagnosticSeverity.Error, "COHDBG001", "Terminal failure.", null, null, 1)]).Encode(), timeout.Token);
            await WriteAsync(channel, ProtocolMessageType.Error,
                new ProtocolErrorMessage(ProtocolErrorCode.ExecutionFailure, "Terminal failure.").Encode(), timeout.Token);
        }, timeout.Token, compositeValues: true);
        await using var connection = await client.RentAsync(timeout.Token);
        var exchange = new GraphPathsExchange("MATCH (n) RETURN n", null);
        Task completion = exchange.RunAsync(connection, timeout.Token);
        await completion.WaitAsync(timeout.Token);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        // Act
        var failure = await Should.ThrowAsync<GraphClientException>(async () =>
            await exchange.ReadPathAsync(completion, cancelled.Token));

        // Assert
        failure.Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHDBG001");
        await server.WaitAsync(timeout.Token);
    }

    [Theory(DisplayName = "Cohesion Test [Graph.Client] - Composite values: malformed rows are protocol violations")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public async Task QueryAsync_MalformedCompositeRow_ShouldDiscardSession(int form)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var listener = new InMemoryConnectionListener();
        await using var client = CreateClient(listener);
        Task server = ServeAsync(listener, async channel =>
        {
            (await ReadAsync(channel, timeout.Token)).Type.ShouldBe((ProtocolMessageType)GraphProtocolMessageType.Execute);
            await WriteAsync(channel, (ProtocolMessageType)GraphProtocolMessageType.ResultHeader,
                new GraphProtocolResultHeaderMessage([("x", (byte)0x80)]).Encode(), timeout.Token);
            byte[] component = form switch
            {
                0 => [255],
                1 => [1, 255, 255, 255, 127],
                _ => System.Linq.Enumerable.Repeat(new byte[] { 1, 1, 0, 0, 0 }, GraphValueCodec.MaxDepth + 1)
                    .SelectMany(bytes => bytes).Concat(GraphValueCodec.Encode(null)).ToArray(),
            };
            byte[] payload = new byte[component.Length + 5];
            payload[0] = 1;
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(1), component.Length);
            component.CopyTo(payload, 5);
            if (form == 3) { payload = new byte[] { 1, 255, 255, 255, 255 }; }
            if (form == 4) { payload = new byte[] { 1, 42, 0, 0, 0 }; }
            if (form == 5) { payload = new byte[] { 0, 0, 99 }; }
            if (form == 6) { payload = new byte[] { 1, 0, 0 }; }
            await WriteAsync(channel, (ProtocolMessageType)GraphProtocolMessageType.ResultRow, payload, timeout.Token);
        }, timeout.Token, compositeValues: true);
        await using var connection = await client.ConnectAsync(timeout.Token);
        // Act
        var error = await Should.ThrowAsync<GraphClientException>(async () => await connection.QueryAsync("RETURN 1", cancellationToken: timeout.Token));
        // Assert
        error.Code.ShouldBe(ProtocolErrorCode.ProtocolViolation);
        connection.IsOpen.ShouldBeFalse();
        await server.WaitAsync(timeout.Token);
    }

    [Fact(DisplayName = "Cohesion Test [Graph.Client] - Composite values: an older Ready refuses parameters without sending a frame")]
    public async Task QueryAsync_UnnegotiatedCompositeParameter_ShouldSendNothingAndKeepSession()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var listener = new InMemoryConnectionListener();
        await using var client = CreateClient(listener);
        Task server = ServeAsync(listener, async channel =>
        {
            var request = await ReadAsync(channel, timeout.Token);
            GraphProtocolExecuteMessage.Decode(request.Payload.Span).Statement.ShouldBe("SHOW LABELS");
            await WriteAsync(channel, (ProtocolMessageType)GraphProtocolMessageType.ResultComplete,
                new GraphProtocolResultCompleteMessage(-1).Encode(), timeout.Token);
        }, timeout.Token);
        await using var connection = await client.ConnectAsync(timeout.Token);
        // Act / Assert
        foreach (object value in new object[] { new List<long> { 1 }, new Dictionary<string, object?> { ["k"] = 1 } })
        {
            var error = await Should.ThrowAsync<GraphClientException>(async () => await connection.QueryAsync("RETURN $p",
                new Dictionary<string, object?> { ["p"] = value }, timeout.Token));
            error.Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);
        }
        (await connection.QueryAsync("SHOW LABELS", cancellationToken: timeout.Token)).ShouldBeEmpty();
        connection.IsOpen.ShouldBeTrue();
        await server.WaitAsync(timeout.Token);
    }

    [Theory(DisplayName = "Cohesion Test [Graph.Client] - Initial non-statement and malformed errors discard the path connection")]
    [InlineData(ProtocolErrorCode.ProtocolViolation, false)]
    [InlineData(ProtocolErrorCode.Unavailable, false)]
    [InlineData(ProtocolErrorCode.ProtocolViolation, true)]
    public async Task QueryPathsAsync_InitialFatalError_ShouldThrowAndDiscard(ProtocolErrorCode code, bool malformed)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var listener = new InMemoryConnectionListener();
        await using var client = CreateClient(listener);
        Task server = ServeAsync(listener, async channel =>
        {
            (await ReadAsync(channel, timeout.Token)).Type.ShouldBe((ProtocolMessageType)GraphProtocolMessageType.ExecutePaths);
            byte[] payload = malformed ? [0] : new ProtocolErrorMessage(code, "Injected initial failure.").Encode();
            await WriteAsync(channel, ProtocolMessageType.Error, payload, timeout.Token);
        }, timeout.Token);
        await using var connection = await client.ConnectAsync(timeout.Token);
        await using var paths = connection.QueryPathsAsync("MATCH (n) RETURN n", cancellationToken: timeout.Token).GetAsyncEnumerator();

        // Act
        var error = await Should.ThrowAsync<GraphClientException>(async () => await paths.MoveNextAsync());

        // Assert
        error.Code.ShouldBe(code);
        connection.IsOpen.ShouldBeFalse();
        await server.WaitAsync(timeout.Token);
    }

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

    private static GraphClient CreateClient(InMemoryConnectionListener listener)
        => GraphClient.Create(new()
        {
            Settings = new DatabaseConnectionSettings { Database = "graph", EndPoint = listener.EndPoint, MaxPoolSize = 1 },
            ConnectionFactory = listener.CreateFactory(),
        });

    private static async Task ServeAsync(InMemoryConnectionListener listener, Func<ProtocolChannel, Task> exchange, CancellationToken token, bool compositeValues = false)
    {
        await using var transport = await listener.AcceptAsync(token);
        await using var channel = new ProtocolChannel(transport.AsStream(), GraphProtocol.Family);
        var startup = await ReadAsync(channel, token);
        startup.Type.ShouldBe(ProtocolMessageType.Startup);
        await WriteAsync(channel, ProtocolMessageType.Authenticate, ReadOnlyMemory<byte>.Empty, token);
        (await ReadAsync(channel, token)).Type.ShouldBe(ProtocolMessageType.AuthenticateResponse);
        var ready = compositeValues ? channel.Negotiate(ProtocolVersion.Current,
            ProtocolStartupMessage.Decode(startup.Payload.Span).Capabilities).Encode() : [];
        await WriteAsync(channel, ProtocolMessageType.Ready, ready, token);
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

