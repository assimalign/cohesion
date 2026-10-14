using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

namespace Assimalign.Cohesion.Http.Connections.Tests;

/// <summary>
/// The token behind an HTTP/2 exchange's <see cref="IHttpContext.RequestCancelled"/> (#1307). HTTP/2
/// links no connection-scoped token into an exchange: when the host gives up on the connection, the
/// frame pump aborts the exchanges still in flight. A linked source per exchange on the connection's
/// token would stay registered there for the connection's life, so a completed exchange must hold no
/// link to it, however many exchanges the connection has served.
/// </summary>
public class Http2ExchangeTokenTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(10);

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 Exchange Token: Completed exchanges should hold no link to the connection's token")]
    public async Task ReceiveAsync_OnManySequentialExchanges_ShouldLinkNoCompletedExchangeToConnectionToken()
    {
        // Arrange — one connection serves many exchanges, one after another.
        const int exchangeCount = 64;
        using CancellationTokenSource host = new();
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync(receiveToken: host.Token);
        List<CancellationToken> completed = new(exchangeCount);

        for (int index = 0; index < exchangeCount; index++)
        {
            await peer.SendHeadersAsync(1 + (index * 2), endStream: true, Http2TestPeer.Get($"/{index}"));
            IHttpContext exchange = await peer.ReceiveContextAsync();
            await peer.ConnectionContext.SendAsync(exchange).AsTask().WaitAsync(_timeout);
            completed.Add(exchange.RequestCancelled);
        }

        // Act — the host gives up on the connection once every exchange has completed.
        host.Cancel();
        await peer.WaitForReceiveEndAsync();

        // Assert — a cancellation that reaches a completed exchange came through a link that
        // outlived it.
        completed.Count(token => token.IsCancellationRequested).ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 Exchange Token: An exchange in flight should be cancelled when the host gives up on the connection")]
    public async Task ReceiveAsync_OnHostCancellationDuringExchange_ShouldCancelExchange()
    {
        // Arrange — the exchange is dispatched and not yet answered.
        using CancellationTokenSource host = new();
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync(receiveToken: host.Token);
        await peer.SendHeadersAsync(1, endStream: true, Http2TestPeer.Get("/pending"));
        IHttpContext exchange = await peer.ReceiveContextAsync();
        TaskCompletionSource cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenRegistration registration = exchange.RequestCancelled.Register(() => cancelled.TrySetResult());

        // Act
        host.Cancel();

        // Assert — the frame pump aborts the exchanges it still holds.
        await cancelled.Task.WaitAsync(_timeout);
        exchange.RequestCancelled.IsCancellationRequested.ShouldBeTrue();
    }
}
