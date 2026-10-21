using System;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.InMemory;
using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Client.Tests;

public sealed class DatabaseClientTerminalErrorTests
{
    [Theory(DisplayName = "Cohesion Test [Database.Client] - Pending terminal responses precede materialized and streaming request writes")]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(4, false)]
    [InlineData(5, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    [InlineData(4, true)]
    [InlineData(5, true)]
    public async Task ExecuteAsync_PendingTerminalResponse_ShouldConsumeBeforeSending(int response, bool streaming)
    {
        // Arrange: the peer queues its terminal response while no request is active.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var listener = new InMemoryConnectionListener();
        await using var client = DatabaseClient.Create(new()
        {
            Settings = new() { Database = "test", EndPoint = listener.EndPoint, MaxPoolSize = 1 },
            Family = StreamingClientTestHarness.Family,
            ConnectionFactory = listener.CreateFactory(),
        });
        Task peer = Task.Run(async () =>
        {
            await using var transport = await listener.AcceptAsync(timeout.Token);
            await using var channel = new ProtocolChannel(transport.AsStream(), StreamingClientTestHarness.Family, leaveOpen: true);
            (await channel.Reader.ReadFrameAsync(timeout.Token)).ShouldNotBeNull().Type.ShouldBe(ProtocolMessageType.Startup);
            await channel.Writer.WriteFrameAsync(new(ProtocolMessageType.Authenticate, ReadOnlyMemory<byte>.Empty), timeout.Token);
            (await channel.Reader.ReadFrameAsync(timeout.Token)).ShouldNotBeNull().Type.ShouldBe(ProtocolMessageType.AuthenticateResponse);
            await channel.Writer.WriteFrameAsync(new(ProtocolMessageType.Ready, ReadOnlyMemory<byte>.Empty), timeout.Token);
            if (response != 3)
            {
                var type = response == 2 ? ProtocolMessageType.Ping : ProtocolMessageType.Error;
                byte[] payload = response switch
                {
                    2 => [],
                    4 => new ProtocolErrorMessage(ProtocolErrorCode.ConnectionFailure, "invalid peer code").Encode(),
                    5 => [0],
                    _ => new ProtocolErrorMessage(ProtocolErrorCode.Unavailable, "terminal notice").Encode(),
                };
                await channel.Writer.WriteFrameAsync(new(type, payload), timeout.Token);
            }
            await channel.Writer.FlushAsync(timeout.Token);
            if (response != 0) { await transport.Output.CompleteAsync(); }
            ready.TrySetResult();
            // The terminal response must be consumed before any model request is sent.
            (await channel.Reader.ReadFrameAsync(timeout.Token)).ShouldBeNull();
        });
        await using var connection = await client.RentAsync(timeout.Token);
        try
        {
            await ready.Task.WaitAsync(timeout.Token);

            // Act
            var error = await Should.ThrowAsync<DatabaseClientException>(async () =>
            {
                if (streaming)
                {
                    await using var stream = await connection.ExecuteStreamingAsync(StreamingClientTestHarness.CreateExchange(), timeout.Token);
                }
                else
                {
                    await connection.ExecuteAsync(StreamingClientTestHarness.CreatePing(), timeout.Token);
                }
            });

            // Assert
            error.Code.ShouldBe(response switch
            {
                0 or 1 => ProtocolErrorCode.Unavailable,
                3 => ProtocolErrorCode.Internal,
                _ => ProtocolErrorCode.ProtocolViolation,
            });
            connection.IsOpen.ShouldBeFalse();
        }
        finally
        {
            await connection.DisposeAsync();
            await peer;
        }
    }
}
