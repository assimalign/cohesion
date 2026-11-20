using System;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Amqp;
using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Amqp.Connections.Tests;

public class AmqpConnectionTests
{
    private static readonly TimeSpan _testTimeout = TimeSpan.FromSeconds(10);

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - ConnectAsync: Should open both ends with the negotiated limits")]
    public async Task ConnectAsync_OnListener_ShouldOpenBothEndsWithNegotiatedLimits()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        AmqpConnectionListenerOptions listenerOptions = new()
        {
            ContainerId = "server",
            MaxFrameSize = 4096,
            ChannelMax = 7,
            IdleTimeout = TimeSpan.FromSeconds(30)
        };
        AmqpConnectionFactoryOptions factoryOptions = new()
        {
            ContainerId = "client",
            MaxFrameSize = 8192,
            ChannelMax = 15,
            IdleTimeout = TimeSpan.Zero
        };

        // Act
        await using AmqpTestHost host = await AmqpTestHost.StartAsync(listenerOptions, factoryOptions, timeout.Token);

        // Assert
        host.Client.State.ShouldBe(ConnectionState.Open);
        host.Server.State.ShouldBe(ConnectionState.Open);
        host.Client.ContainerId.ShouldBe("client");
        host.Client.RemoteContainerId.ShouldBe("server");
        host.Server.RemoteContainerId.ShouldBe("client");
        host.Server.RemoteHostName.ShouldBe("amqp.test");
        host.Client.MaxFrameSize.ShouldBe(4096u);
        host.Server.MaxFrameSize.ShouldBe(4096u);
        host.Client.ChannelMax.ShouldBe((ushort)7);
        host.Server.ChannelMax.ShouldBe((ushort)7);
        host.Client.RemoteIdleTimeout.ShouldBe(TimeSpan.FromSeconds(15));
        host.Server.RemoteIdleTimeout.ShouldBe(TimeSpan.Zero);
        host.Listener.Connections.ShouldContain(host.Server);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - BeginSessionAsync: Should begin the session on both ends")]
    public async Task BeginSessionAsync_OnOpenConnection_ShouldBeginSessionOnBothEnds()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpTestHost host = await AmqpTestHost.StartAsync(cancellationToken: timeout.Token);
        AmqpSessionOptions options = new()
        {
            Properties = new System.Collections.Generic.Dictionary<AmqpSymbol, object?> { [new AmqpSymbol("tenant")] = "blue" }
        };

        // Act
        (IAmqpSession client, IAmqpSession server) = await host.BeginSessionAsync(options, timeout.Token);

        // Assert
        client.Connection.ShouldBeSameAs(host.Client);
        server.Connection.ShouldBeSameAs(host.Server);
        server.RemoteProperties.ShouldNotBeNull();
        server.RemoteProperties![new AmqpSymbol("tenant")].ShouldBe("blue");
        client.SessionEnded.IsCancellationRequested.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - SendAsync: Should complete with the outcome the receiver settled with")]
    public async Task SendAsync_OnAcceptedDelivery_ShouldCompleteWithAcceptedOutcome()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpTestHost host = await AmqpTestHost.StartAsync(cancellationToken: timeout.Token);
        (IAmqpSession clientSession, IAmqpSession serverSession) = await host.BeginSessionAsync(cancellationToken: timeout.Token);
        (IAmqpSender sender, IAmqpReceiver receiver) = await AmqpTestHost.AttachAsync(clientSession, serverSession, cancellationToken: timeout.Token);
        AmqpMessage message = new()
        {
            Properties = new AmqpProperties { MessageId = "m-1", Subject = "greeting" },
            Body = new AmqpValueBody("hello")
        };

        // Act
        Task<AmqpDeliveryState?> send = sender.SendAsync(message, cancellationToken: timeout.Token).AsTask();
        IAmqpDelivery delivery = await receiver.ReceiveAsync(timeout.Token);
        AmqpMessage received = delivery.GetMessage();
        await delivery.AcceptAsync(timeout.Token);
        AmqpDeliveryState? outcome = await send;

        // Assert
        received.Properties!.MessageId.ShouldBe("m-1");
        received.Properties.Subject.ShouldBe("greeting");
        received.Body.ShouldBeOfType<AmqpValueBody>().Value.ShouldBe("hello");
        outcome.ShouldBeOfType<AmqpAccepted>();
        delivery.IsSettled.ShouldBeTrue();
        receiver.Target.ShouldBeOfType<AmqpTarget>().Address.ShouldBe("queue");
        receiver.Source!.Address.ShouldBe("client");
    }

    [Theory(DisplayName = "Cohesion Test [Amqp.Connections] - SendAsync: Should complete with the receiver's terminal outcome")]
    [InlineData("rejected")]
    [InlineData("released")]
    [InlineData("modified")]
    public async Task SendAsync_OnTerminalOutcome_ShouldCompleteWithThatOutcome(string kind)
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpTestHost host = await AmqpTestHost.StartAsync(cancellationToken: timeout.Token);
        (IAmqpSession clientSession, IAmqpSession serverSession) = await host.BeginSessionAsync(cancellationToken: timeout.Token);
        (IAmqpSender sender, IAmqpReceiver receiver) = await AmqpTestHost.AttachAsync(clientSession, serverSession, cancellationToken: timeout.Token);

        // Act
        Task<AmqpDeliveryState?> send = sender.SendAsync(new byte[] { 1, 2, 3 }, cancellationToken: timeout.Token).AsTask();
        IAmqpDelivery delivery = await receiver.ReceiveAsync(timeout.Token);

        switch (kind)
        {
            case "rejected":
                await delivery.RejectAsync(new AmqpError(AmqpErrorCondition.NotAllowed, "no"), timeout.Token);
                break;
            case "released":
                await delivery.ReleaseAsync(timeout.Token);
                break;
            default:
                await delivery.ModifyAsync(deliveryFailed: true, undeliverableHere: true, cancellationToken: timeout.Token);
                break;
        }

        AmqpDeliveryState? outcome = await send;

        // Assert
        switch (kind)
        {
            case "rejected":
                outcome.ShouldBeOfType<AmqpRejected>().Error!.Condition.ShouldBe(AmqpErrorCondition.NotAllowed);
                break;
            case "released":
                outcome.ShouldBeOfType<AmqpReleased>();
                break;
            default:
                AmqpModified modified = outcome.ShouldBeOfType<AmqpModified>();
                modified.DeliveryFailed.ShouldBeTrue();
                modified.UndeliverableHere.ShouldBeTrue();
                break;
        }
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - SendAsync: Should refuse a message the codec refuses without sending anything")]
    public async Task SendAsync_WithUnencodableMessage_ShouldThrowWithoutSending()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpTestHost host = await AmqpTestHost.StartAsync(cancellationToken: timeout.Token);
        (IAmqpSession clientSession, IAmqpSession serverSession) = await host.BeginSessionAsync(cancellationToken: timeout.Token);
        (IAmqpSender sender, IAmqpReceiver receiver) = await AmqpTestHost.AttachAsync(clientSession, serverSession, cancellationToken: timeout.Token);
        await sender.WaitForCreditAsync(timeout.Token);
        uint credit = sender.Credit;
        AmqpMessage refused = new()
        {
            ApplicationProperties = new System.Collections.Generic.Dictionary<string, object?> { ["tags"] = new[] { "a", "b" } },
            Body = new AmqpValueBody("refused")
        };

        // Act
        await Should.ThrowAsync<ArgumentException>(async () => await sender.SendAsync(refused, cancellationToken: timeout.Token));
        uint creditAfterRefusal = sender.Credit;
        Task<AmqpDeliveryState?> send = sender.SendAsync(new AmqpMessage { Body = new AmqpValueBody("sent") }, cancellationToken: timeout.Token).AsTask();
        IAmqpDelivery delivery = await receiver.ReceiveAsync(timeout.Token);
        AmqpMessage received = delivery.GetMessage();
        await delivery.AcceptAsync(timeout.Token);
        await send;

        // Assert
        creditAfterRefusal.ShouldBe(credit);
        received.Body.ShouldBeOfType<AmqpValueBody>().Value.ShouldBe("sent");
        host.Client.State.ShouldBe(ConnectionState.Open);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - SendAsync: Should complete at once on a link that sends settled")]
    public async Task SendAsync_OnSettledLink_ShouldCompleteWithoutOutcome()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpTestHost host = await AmqpTestHost.StartAsync(cancellationToken: timeout.Token);
        (IAmqpSession clientSession, IAmqpSession serverSession) = await host.BeginSessionAsync(cancellationToken: timeout.Token);
        AmqpSenderOptions options = new()
        {
            Name = "settled",
            Target = new AmqpTarget { Address = "queue" },
            SenderSettleMode = AmqpSenderSettleMode.Settled
        };
        (IAmqpSender sender, IAmqpReceiver receiver) = await AmqpTestHost.AttachAsync(clientSession, serverSession, options, cancellationToken: timeout.Token);

        // Act
        AmqpDeliveryState? outcome = await sender.SendAsync(new byte[] { 42 }, cancellationToken: timeout.Token);
        IAmqpDelivery delivery = await receiver.ReceiveAsync(timeout.Token);

        // Assert
        outcome.ShouldBeNull();
        delivery.IsSettled.ShouldBeTrue();
        delivery.Payload.ToArray().ShouldBe(new byte[] { 42 });
        receiver.SenderSettleMode.ShouldBe(AmqpSenderSettleMode.Settled);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - SendAsync: Should split a large message into frames the receiver reassembles")]
    public async Task SendAsync_OnMessageLargerThanFrame_ShouldReassembleDelivery()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpTestHost host = await AmqpTestHost.StartAsync(
            new AmqpConnectionListenerOptions { MaxFrameSize = 512 },
            new AmqpConnectionFactoryOptions { MaxFrameSize = 512 },
            timeout.Token);
        (IAmqpSession clientSession, IAmqpSession serverSession) = await host.BeginSessionAsync(cancellationToken: timeout.Token);
        (IAmqpSender sender, IAmqpReceiver receiver) = await AmqpTestHost.AttachAsync(clientSession, serverSession, cancellationToken: timeout.Token);
        byte[] payload = new byte[100_000];
        new Random(7).NextBytes(payload);

        // Act
        Task<AmqpDeliveryState?> send = sender.SendAsync(payload, cancellationToken: timeout.Token).AsTask();
        IAmqpDelivery delivery = await receiver.ReceiveAsync(timeout.Token);
        await delivery.AcceptAsync(timeout.Token);
        AmqpDeliveryState? outcome = await send;

        // Assert
        delivery.Payload.ToArray().ShouldBe(payload);
        outcome.ShouldBeOfType<AmqpAccepted>();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - SendAsync: Should deliver many messages in order")]
    public async Task SendAsync_OnManyMessages_ShouldDeliverInOrder()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpTestHost host = await AmqpTestHost.StartAsync(cancellationToken: timeout.Token);
        (IAmqpSession clientSession, IAmqpSession serverSession) = await host.BeginSessionAsync(
            new AmqpSessionOptions { IncomingWindow = 8 },
            timeout.Token);
        (IAmqpSender sender, IAmqpReceiver receiver) = await AmqpTestHost.AttachAsync(
            clientSession,
            serverSession,
            receiverOptions: new AmqpReceiverOptions { Prefetch = 5 },
            cancellationToken: timeout.Token);
        const int count = 500;

        // Act
        Task sending = Task.Run(async () =>
        {
            for (int index = 0; index < count; index++)
            {
                await sender.SendAsync(BitConverter.GetBytes(index), new AmqpDeliveryOptions { Settled = true }, timeout.Token);
            }
        });

        int[] received = new int[count];

        for (int index = 0; index < count; index++)
        {
            IAmqpDelivery delivery = await receiver.ReceiveAsync(timeout.Token);
            received[index] = BitConverter.ToInt32(delivery.Payload.Span);
        }

        await sending;

        // Assert
        for (int index = 0; index < count; index++)
        {
            received[index].ShouldBe(index);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - ReceiveAsync: Should wait for credit the application grants on a link without prefetch")]
    public async Task ReceiveAsync_WithoutPrefetch_ShouldDeliverOnlyGrantedCredit()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpTestHost host = await AmqpTestHost.StartAsync(cancellationToken: timeout.Token);
        (IAmqpSession clientSession, IAmqpSession serverSession) = await host.BeginSessionAsync(cancellationToken: timeout.Token);
        (IAmqpSender sender, IAmqpReceiver receiver) = await AmqpTestHost.AttachAsync(
            clientSession,
            serverSession,
            receiverOptions: new AmqpReceiverOptions { Prefetch = 0 },
            cancellationToken: timeout.Token);

        // Act
        Task<AmqpDeliveryState?> first = sender.SendAsync(new byte[] { 1 }, new AmqpDeliveryOptions { Settled = true }, timeout.Token).AsTask();
        await Task.Delay(100, timeout.Token);
        bool sentWithoutCredit = first.IsCompleted;
        receiver.AddCredit(1);
        await first;
        IAmqpDelivery delivery = await receiver.ReceiveAsync(timeout.Token);

        // Assert
        sentWithoutCredit.ShouldBeFalse();
        delivery.Payload.ToArray().ShouldBe(new byte[] { 1 });
        receiver.Credit.ShouldBe(0u);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - DrainAsync: Should end with no credit when the sender has nothing to send")]
    public async Task DrainAsync_OnIdleSender_ShouldReturnCredit()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpTestHost host = await AmqpTestHost.StartAsync(cancellationToken: timeout.Token);
        (IAmqpSession clientSession, IAmqpSession serverSession) = await host.BeginSessionAsync(cancellationToken: timeout.Token);
        (IAmqpSender sender, IAmqpReceiver receiver) = await AmqpTestHost.AttachAsync(
            clientSession,
            serverSession,
            receiverOptions: new AmqpReceiverOptions { Prefetch = 0 },
            cancellationToken: timeout.Token);
        receiver.AddCredit(10);
        await sender.WaitForCreditAsync(timeout.Token);

        // Act
        await receiver.DrainAsync(timeout.Token);

        // Assert
        receiver.Credit.ShouldBe(0u);
        sender.Credit.ShouldBe(0u);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - AcceptAsync: Should wait for the sender to settle on a link that settles second")]
    public async Task AcceptAsync_OnReceiverSettleModeSecond_ShouldCompleteWhenSenderSettles()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpTestHost host = await AmqpTestHost.StartAsync(cancellationToken: timeout.Token);
        (IAmqpSession clientSession, IAmqpSession serverSession) = await host.BeginSessionAsync(cancellationToken: timeout.Token);
        AmqpSenderOptions senderOptions = new()
        {
            Name = "second",
            Target = new AmqpTarget { Address = "queue" },
            ReceiverSettleMode = AmqpReceiverSettleMode.Second
        };
        (IAmqpSender sender, IAmqpReceiver receiver) = await AmqpTestHost.AttachAsync(
            clientSession,
            serverSession,
            senderOptions,
            new AmqpReceiverOptions { ReceiverSettleMode = AmqpReceiverSettleMode.Second },
            timeout.Token);

        // Act
        Task<AmqpDeliveryState?> send = sender.SendAsync(new byte[] { 9 }, cancellationToken: timeout.Token).AsTask();
        IAmqpDelivery delivery = await receiver.ReceiveAsync(timeout.Token);
        await delivery.AcceptAsync(timeout.Token);
        AmqpDeliveryState? outcome = await send;

        // Assert
        sender.ReceiverSettleMode.ShouldBe(AmqpReceiverSettleMode.Second);
        outcome.ShouldBeOfType<AmqpAccepted>();
        delivery.IsSettled.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - RejectAsync: Should refuse the link the peer attached")]
    public async Task RejectAsync_OnLinkRequest_ShouldFailAttachWithError()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpTestHost host = await AmqpTestHost.StartAsync(cancellationToken: timeout.Token);
        (IAmqpSession clientSession, IAmqpSession serverSession) = await host.BeginSessionAsync(cancellationToken: timeout.Token);
        Task<IAmqpSender> attach = clientSession.AttachSenderAsync(
            new AmqpSenderOptions { Target = new AmqpTarget { Address = "missing" } },
            timeout.Token).AsTask();

        // Act
        IAmqpLinkRequest request = await serverSession.AcceptLinkAsync(timeout.Token);
        await request.RejectAsync(new AmqpError(AmqpErrorCondition.NotFound, "no such node"), timeout.Token);
        AmqpException exception = await Should.ThrowAsync<AmqpException>(attach);

        // Assert
        request.Role.ShouldBe(AmqpRole.Receiver);
        request.Target.ShouldBeOfType<AmqpTarget>().Address.ShouldBe("missing");
        exception.ErrorCode.ShouldBe(AmqpErrorCode.LinkDetached);
        exception.Error!.Condition.ShouldBe(AmqpErrorCondition.NotFound);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - AcceptReceiverAsync: Should refuse to accept a request in the wrong role")]
    public async Task AcceptSenderAsync_OnPeerSender_ShouldThrowInvalidOperationException()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpTestHost host = await AmqpTestHost.StartAsync(cancellationToken: timeout.Token);
        (IAmqpSession clientSession, IAmqpSession serverSession) = await host.BeginSessionAsync(cancellationToken: timeout.Token);
        _ = clientSession.AttachSenderAsync(new AmqpSenderOptions { Target = new AmqpTarget() }, timeout.Token).AsTask();
        IAmqpLinkRequest request = await serverSession.AcceptLinkAsync(timeout.Token);

        // Act
        InvalidOperationException exception = Should.Throw<InvalidOperationException>(
            () => request.AcceptSenderAsync(cancellationToken: timeout.Token));

        // Assert
        exception.ShouldNotBeNull();
        IAmqpReceiver receiver = await request.AcceptReceiverAsync(cancellationToken: timeout.Token);
        receiver.Role.ShouldBe(AmqpRole.Receiver);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - DetachAsync: Should detach the link on both ends and fail its unsettled deliveries")]
    public async Task CloseAsync_OnReceiver_ShouldDetachSenderAndFailUnsettledDelivery()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpTestHost host = await AmqpTestHost.StartAsync(cancellationToken: timeout.Token);
        (IAmqpSession clientSession, IAmqpSession serverSession) = await host.BeginSessionAsync(cancellationToken: timeout.Token);
        (IAmqpSender sender, IAmqpReceiver receiver) = await AmqpTestHost.AttachAsync(clientSession, serverSession, cancellationToken: timeout.Token);
        Task<AmqpDeliveryState?> send = sender.SendAsync(new byte[] { 5 }, cancellationToken: timeout.Token).AsTask();
        await receiver.ReceiveAsync(timeout.Token);

        // Act
        await receiver.CloseAsync(new AmqpError(AmqpErrorCondition.ResourceDeleted, "gone"), timeout.Token);
        AmqpException exception = await Should.ThrowAsync<AmqpException>(send);

        // Assert
        exception.ErrorCode.ShouldBe(AmqpErrorCode.LinkDetached);
        exception.Error!.Condition.ShouldBe(AmqpErrorCondition.ResourceDeleted);
        sender.LinkDetached.IsCancellationRequested.ShouldBeTrue();
        receiver.LinkDetached.IsCancellationRequested.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - EndAsync: Should end the session on both ends and detach its links")]
    public async Task EndAsync_OnSession_ShouldEndPeerSessionAndDetachLinks()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpTestHost host = await AmqpTestHost.StartAsync(cancellationToken: timeout.Token);
        (IAmqpSession clientSession, IAmqpSession serverSession) = await host.BeginSessionAsync(cancellationToken: timeout.Token);
        (IAmqpSender sender, IAmqpReceiver receiver) = await AmqpTestHost.AttachAsync(clientSession, serverSession, cancellationToken: timeout.Token);

        // Act
        await clientSession.EndAsync(cancellationToken: timeout.Token);
        AmqpException exception = await Should.ThrowAsync<AmqpException>(receiver.ReceiveAsync(timeout.Token).AsTask());

        // Assert
        clientSession.SessionEnded.IsCancellationRequested.ShouldBeTrue();
        serverSession.SessionEnded.IsCancellationRequested.ShouldBeTrue();
        sender.LinkDetached.IsCancellationRequested.ShouldBeTrue();
        receiver.LinkDetached.IsCancellationRequested.ShouldBeTrue();
        exception.ErrorCode.ShouldBe(AmqpErrorCode.LinkDetached);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - CloseAsync: Should close both ends and report the error to the peer")]
    public async Task CloseAsync_WithError_ShouldCloseBothEnds()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpTestHost host = await AmqpTestHost.StartAsync(cancellationToken: timeout.Token);
        (IAmqpSession clientSession, IAmqpSession serverSession) = await host.BeginSessionAsync(cancellationToken: timeout.Token);

        // Act
        await host.Server.CloseAsync(new AmqpError(AmqpErrorCondition.ConnectionForced, "maintenance"), timeout.Token);
        AmqpException exception = await Should.ThrowAsync<AmqpException>(host.Client.BeginSessionAsync(cancellationToken: timeout.Token).AsTask());

        // Assert
        host.Server.State.ShouldBe(ConnectionState.Closed);
        host.Client.ConnectionClosed.IsCancellationRequested.ShouldBeTrue();
        clientSession.SessionEnded.IsCancellationRequested.ShouldBeTrue();
        serverSession.SessionEnded.IsCancellationRequested.ShouldBeTrue();
        exception.ErrorCode.ShouldBe(AmqpErrorCode.ConnectionClosed);
        exception.Error!.Condition.ShouldBe(AmqpErrorCondition.ConnectionForced);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - IdleTimeout: Should keep an idle connection open with empty frames")]
    public async Task IdleTimeout_OnIdleConnection_ShouldKeepConnectionOpen()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        AmqpConnectionListenerOptions listenerOptions = new() { IdleTimeout = TimeSpan.FromMilliseconds(200) };
        AmqpConnectionFactoryOptions factoryOptions = new() { IdleTimeout = TimeSpan.FromMilliseconds(200) };
        await using AmqpTestHost host = await AmqpTestHost.StartAsync(listenerOptions, factoryOptions, timeout.Token);

        // Act
        await Task.Delay(1000, timeout.Token);

        // Assert
        host.Client.State.ShouldBe(ConnectionState.Open);
        host.Server.State.ShouldBe(ConnectionState.Open);
    }
}
