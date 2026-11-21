using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Amqp;

using NetLite = Amqp;
using NetLiteFraming = Amqp.Framing;
using NetLiteTypes = Amqp.Types;

namespace Assimalign.Cohesion.Amqp.Connections.Tests;

/// <summary>
/// AMQPNetLite.Core's <c>ContainerHost</c> as the broker, Cohesion's connection factory as the client, over TCP
/// loopback (#432): Cohesion opens the connection, begins the session and attaches the links. Each test prints the
/// frames the peer sent and received; <c>docs/DESIGN.md</c>, "Interoperability", lists what the suite covers and
/// what the peer does not support.
/// </summary>
[Collection(AmqpInteropCollection.Name)]
[Trait("Category", AmqpInteropCollection.Category)]
public sealed class AmqpNetLiteHostInteropTests : IDisposable
{
    private static readonly TimeSpan _testTimeout = TimeSpan.FromSeconds(20);

    private readonly AmqpNetLiteTrace _trace;

    public AmqpNetLiteHostInteropTests(ITestOutputHelper output)
    {
        _trace = AmqpNetLiteTrace.Attach(output);
    }

    public void Dispose()
    {
        _trace.Dispose();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Interop ConnectAsync: Should open a connection to an AMQPNetLite host over SASL PLAIN")]
    public async Task ConnectAsync_ToNetLiteHostOverSaslPlain_ShouldOpenBothEnds()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        using AmqpNetLiteHost host = AmqpNetLiteHost.Start(maxFrameSize: 16 * 1024);

        // Act
        await using IAmqpConnection client = await host.ConnectAsync(timeout.Token);

        // Assert
        client.RemoteContainerId.ShouldBe(AmqpNetLiteHost.ContainerId);
        client.MaxFrameSize.ShouldBe(16u * 1024);
        client.AmqpSasl.ShouldNotBeNull().Mechanism.ShouldBe(new AmqpSymbol("PLAIN"));
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Interop ConnectAsync: Should open a connection to an AMQPNetLite host over SASL ANONYMOUS")]
    public async Task ConnectAsync_ToNetLiteHostOverSaslAnonymous_ShouldOpenBothEnds()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        using AmqpNetLiteHost host = AmqpNetLiteHost.Start();

        // Act
        await using IAmqpConnection client = await host.ConnectAsync(new AmqpSaslAnonymousClientMechanism(), timeout.Token);

        // Assert
        client.RemoteContainerId.ShouldBe(AmqpNetLiteHost.ContainerId);
        client.AmqpSasl.ShouldNotBeNull().Mechanism.ShouldBe(new AmqpSymbol("ANONYMOUS"));
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Interop ConnectAsync: Should fail when an AMQPNetLite host refuses the password")]
    public async Task ConnectAsync_WithWrongPasswordToNetLiteHost_ShouldFailTheLogin()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        using AmqpNetLiteHost host = AmqpNetLiteHost.Start();
        IAmqpSaslClientMechanism mechanism = AmqpSaslPlainClientMechanism.Create(AmqpNetLiteHost.UserName, "wrong-password");

        // Act
        AmqpException refusal = await Should.ThrowAsync<AmqpException>(host.ConnectAsync(mechanism, timeout.Token).AsTask());

        // Assert
        refusal.ErrorCode.ShouldBe(AmqpErrorCode.AuthenticationFailed);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Interop CloseAsync: Should end a connection the AMQPNetLite host answers")]
    public async Task CloseAsync_ToNetLiteHost_ShouldEndTheConnection()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        using AmqpNetLiteHost host = AmqpNetLiteHost.Start();
        await using IAmqpConnection client = await host.ConnectAsync(timeout.Token);

        // Act
        await client.CloseAsync(cancellationToken: timeout.Token);

        // Assert
        client.ConnectionClosed.IsCancellationRequested.ShouldBeTrue();
        AmqpException closed = await Should.ThrowAsync<AmqpException>(client.BeginSessionAsync(cancellationToken: timeout.Token).AsTask());
        closed.ErrorCode.ShouldBe(AmqpErrorCode.ConnectionClosed);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Interop SendAsync: Should complete with each outcome an AMQPNetLite processor settles with")]
    public async Task SendAsync_ToNetLiteProcessor_ShouldCompleteWithEachOutcome()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        using AmqpNetLiteHost host = AmqpNetLiteHost.Start();
        TestMessageProcessor processor = new()
        {
            Settle = message => (string)message.Body switch
            {
                "reject" => new NetLiteFraming.Rejected
                {
                    Error = new NetLiteFraming.Error(NetLite.ErrorCode.NotAllowed) { Description = "refused by the peer" }
                },
                "release" => new NetLiteFraming.Released(),
                "modify" => new NetLiteFraming.Modified
                {
                    DeliveryFailed = true,
                    UndeliverableHere = true,
                    MessageAnnotations = new NetLiteTypes.Fields { [new NetLiteTypes.Symbol("x-opt-retry")] = 3 }
                },
                _ => null
            }
        };
        host.Host.RegisterMessageProcessor("outcomes", processor);
        await using IAmqpConnection client = await host.ConnectAsync(timeout.Token);
        IAmqpSession session = await client.BeginSessionAsync(cancellationToken: timeout.Token);
        IAmqpSender sender = await AttachSenderAsync(session, "outcomes", timeout.Token);
        string[] bodies = ["accept", "reject", "release", "modify"];
        List<AmqpDeliveryState?> states = [];

        // Act
        foreach (string body in bodies)
        {
            states.Add(await sender.SendAsync(new AmqpMessage { Body = new AmqpValueBody(body) }, cancellationToken: timeout.Token));
        }

        // Assert
        states[0].ShouldBeOfType<AmqpAccepted>();
        AmqpError error = states[1].ShouldBeOfType<AmqpRejected>().Error.ShouldNotBeNull();
        error.Condition.ShouldBe(AmqpErrorCondition.NotAllowed);
        error.Description.ShouldBe("refused by the peer");
        states[2].ShouldBeOfType<AmqpReleased>();
        AmqpModified modified = states[3].ShouldBeOfType<AmqpModified>();
        modified.DeliveryFailed.ShouldBeTrue();
        modified.UndeliverableHere.ShouldBeTrue();
        modified.MessageAnnotations.ShouldNotBeNull()[new AmqpSymbol("x-opt-retry")].ShouldBe(3);

        foreach (string body in bodies)
        {
            (await processor.ReceiveAsync(timeout.Token)).Body.ShouldBe(body);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Interop SendAsync: Should stay within an AMQPNetLite processor's credit and keep the order")]
    public async Task SendAsync_ManyToNetLiteProcessor_ShouldStayWithinItsCreditAndKeepTheOrder()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        using AmqpNetLiteHost host = AmqpNetLiteHost.Start();
        TestMessageProcessor processor = new() { Credit = 5 };
        host.Host.RegisterMessageProcessor("credit", processor);
        await using IAmqpConnection client = await host.ConnectAsync(timeout.Token);
        IAmqpSession session = await client.BeginSessionAsync(cancellationToken: timeout.Token);
        IAmqpSender sender = await AttachSenderAsync(session, "credit", timeout.Token);
        List<Task<AmqpDeliveryState?>> sends = [];

        // Act: ten times the credit, all at once; the peer fails the link if a transfer exceeds its credit.
        for (int index = 0; index < 50; index++)
        {
            sends.Add(sender.SendAsync(new AmqpMessage { Body = new AmqpValueBody(index) }, cancellationToken: timeout.Token).AsTask());
        }

        AmqpDeliveryState?[] states = await Task.WhenAll(sends).WaitAsync(timeout.Token);

        // Assert
        states.ShouldAllBe(state => state is AmqpAccepted);

        for (int index = 0; index < 50; index++)
        {
            (await processor.ReceiveAsync(timeout.Token)).Body.ShouldBe(index);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Interop SendAsync: Should split a message across frames so an AMQPNetLite host reassembles it")]
    public async Task SendAsync_LargerThanTheFrameToNetLiteHost_ShouldReassembleIt()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        byte[] payload = CreatePayload(256 * 1024);
        using AmqpNetLiteHost host = AmqpNetLiteHost.Start(maxFrameSize: 4096);
        TestMessageProcessor processor = new();
        host.Host.RegisterMessageProcessor("split", processor);
        await using IAmqpConnection client = await host.ConnectAsync(timeout.Token, maxFrameSize: 4096);
        IAmqpSession session = await client.BeginSessionAsync(cancellationToken: timeout.Token);
        IAmqpSender sender = await AttachSenderAsync(session, "split", timeout.Token);

        // Act
        AmqpDeliveryState? state = await sender.SendAsync(new AmqpMessage { Body = new AmqpDataBody(payload) }, cancellationToken: timeout.Token);
        NetLite.Message received = await processor.ReceiveAsync(timeout.Token);

        // Assert
        client.MaxFrameSize.ShouldBe(4096u);
        state.ShouldBeOfType<AmqpAccepted>();
        byte[] body = received.BodySection.ShouldBeOfType<NetLiteFraming.Data>().Binary;
        body.AsSpan().SequenceEqual(payload).ShouldBeTrue("the body to arrive byte for byte");
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Interop ReceiveAsync: Should report each settlement to the AMQPNetLite source")]
    public async Task ReceiveAsync_FromNetLiteSource_ShouldReportEachSettlementToThePeer()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        using AmqpNetLiteHost host = AmqpNetLiteHost.Start();
        TestMessageSource source = new();
        host.Host.RegisterMessageSource("settlements", source);
        AmqpSymbol retry = new("x-opt-retry");
        Func<IAmqpDelivery, ValueTask>[] settlements =
        [
            delivery => delivery.AcceptAsync(timeout.Token),
            delivery => delivery.RejectAsync(new AmqpError(AmqpErrorCondition.NotAllowed, "refused by the client"), timeout.Token),
            delivery => delivery.ReleaseAsync(timeout.Token),
            delivery => delivery.ModifyAsync(
                deliveryFailed: true,
                undeliverableHere: true,
                messageAnnotations: new Dictionary<AmqpSymbol, object?> { [retry] = 1 },
                cancellationToken: timeout.Token)
        ];

        for (int index = 0; index < settlements.Length; index++)
        {
            source.Enqueue(new NetLite.Message($"message {index}"));
        }

        await using IAmqpConnection client = await host.ConnectAsync(timeout.Token);
        IAmqpSession session = await client.BeginSessionAsync(cancellationToken: timeout.Token);
        IAmqpReceiver receiver = await AttachReceiverAsync(session, "settlements", 10, timeout.Token);
        List<string?> bodies = [];
        List<NetLiteFraming.DeliveryState?> states = [];

        // Act
        for (int index = 0; index < settlements.Length; index++)
        {
            IAmqpDelivery delivery = await receiver.ReceiveAsync(timeout.Token);

            bodies.Add(delivery.GetMessage().Body.ShouldBeOfType<AmqpValueBody>().Value as string);
            await settlements[index](delivery);
            states.Add(await source.ReceiveDispositionAsync(timeout.Token));
        }

        // Assert
        bodies.ShouldBe(["message 0", "message 1", "message 2", "message 3"]);
        states[0].ShouldBeOfType<NetLiteFraming.Accepted>();
        NetLiteFraming.Error error = states[1].ShouldBeOfType<NetLiteFraming.Rejected>().Error;
        error.Condition.ToString().ShouldBe(NetLite.ErrorCode.NotAllowed);
        error.Description.ShouldBe("refused by the client");
        states[2].ShouldBeOfType<NetLiteFraming.Released>();
        NetLiteFraming.Modified modified = states[3].ShouldBeOfType<NetLiteFraming.Modified>();
        modified.DeliveryFailed.ShouldBeTrue();
        modified.UndeliverableHere.ShouldBeTrue();
        modified.MessageAnnotations[new NetLiteTypes.Symbol("x-opt-retry")].ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Interop ReceiveAsync: Should take many AMQPNetLite messages in order within its prefetch")]
    public async Task ReceiveAsync_ManyFromNetLiteSource_ShouldKeepTheirOrder()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        using AmqpNetLiteHost host = AmqpNetLiteHost.Start();
        TestMessageSource source = new();
        host.Host.RegisterMessageSource("many", source);

        for (int index = 0; index < 300; index++)
        {
            source.Enqueue(new NetLite.Message(index));
        }

        await using IAmqpConnection client = await host.ConnectAsync(timeout.Token);
        IAmqpSession session = await client.BeginSessionAsync(cancellationToken: timeout.Token);
        IAmqpReceiver receiver = await AttachReceiverAsync(session, "many", 50, timeout.Token);
        List<object?> bodies = [];

        // Act
        for (int index = 0; index < 300; index++)
        {
            IAmqpDelivery delivery = await receiver.ReceiveAsync(timeout.Token);

            bodies.Add(delivery.GetMessage().Body.ShouldBeOfType<AmqpValueBody>().Value);
            await delivery.AcceptAsync(timeout.Token);
        }

        // Assert
        for (int index = 0; index < 300; index++)
        {
            bodies[index].ShouldBe(index);
        }

        receiver.Credit.ShouldBeLessThanOrEqualTo(50u);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Interop ReceiveAsync: Should reassemble a message an AMQPNetLite host splits across frames")]
    public async Task ReceiveAsync_LargerThanTheFrameFromNetLiteHost_ShouldReassembleIt()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        byte[] payload = CreatePayload(256 * 1024);
        using AmqpNetLiteHost host = AmqpNetLiteHost.Start(maxFrameSize: 4096);
        TestMessageSource source = new();
        host.Host.RegisterMessageSource("split", source);
        source.Enqueue(new NetLite.Message { BodySection = new NetLiteFraming.Data { Binary = payload } });
        await using IAmqpConnection client = await host.ConnectAsync(timeout.Token, maxFrameSize: 4096);
        IAmqpSession session = await client.BeginSessionAsync(cancellationToken: timeout.Token);
        IAmqpReceiver receiver = await AttachReceiverAsync(session, "split", 1, timeout.Token);

        // Act
        IAmqpDelivery delivery = await receiver.ReceiveAsync(timeout.Token);
        AmqpMessage received = delivery.GetMessage();
        await delivery.AcceptAsync(timeout.Token);
        NetLiteFraming.DeliveryState? state = await source.ReceiveDispositionAsync(timeout.Token);

        // Assert
        AmqpDataBody body = received.Body.ShouldBeOfType<AmqpDataBody>();
        body.Sections.Count.ShouldBe(1);
        body.Sections[0].Span.SequenceEqual(payload).ShouldBeTrue("the body to arrive byte for byte");
        state.ShouldBeOfType<NetLiteFraming.Accepted>();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Interop AttachSenderAsync: Should fail with the error an AMQPNetLite host refuses the link with")]
    public async Task AttachSenderAsync_ToUnknownNetLiteAddress_ShouldFailWithThePeersError()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        using AmqpNetLiteHost host = AmqpNetLiteHost.Start();
        await using IAmqpConnection client = await host.ConnectAsync(timeout.Token);
        IAmqpSession session = await client.BeginSessionAsync(cancellationToken: timeout.Token);

        // Act
        AmqpException refused = await Should.ThrowAsync<AmqpException>(AttachSenderAsync(session, "no-such-node", timeout.Token).AsTask());

        // Assert
        refused.ErrorCode.ShouldBe(AmqpErrorCode.LinkDetached);
        refused.Error.ShouldNotBeNull().Condition.ShouldBe(AmqpErrorCondition.NotFound);
    }

    private static byte[] CreatePayload(int length)
    {
        byte[] payload = new byte[length];

        for (int index = 0; index < payload.Length; index++)
        {
            payload[index] = (byte)(index * 31 + 7);
        }

        return payload;
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Interop DetachAsync: Should attach again and close a link the AMQPNetLite host closes in answer to its suspending detach")]
    public async Task DetachAsync_OnLinkTheNetLiteHostCloses_ShouldAttachItAgainAndCloseIt()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        using AmqpNetLiteHost host = AmqpNetLiteHost.Start();
        host.Host.RegisterMessageProcessor("suspend", new TestMessageProcessor());
        await using IAmqpConnection client = await host.ConnectAsync(timeout.Token);
        IAmqpSession session = await client.BeginSessionAsync(cancellationToken: timeout.Token);
        IAmqpSender sender = await AttachSenderAsync(session, "suspend", timeout.Token);

        // Act: AMQPNetLite answers a detach that does not close its link with one that does. A second attach of the
        // name then shows the exchange released it.
        await sender.DetachAsync(cancellationToken: timeout.Token);
        IReadOnlyList<string> frames = _trace.LinkFrames;
        IAmqpSender again = await AttachSenderAsync(session, "suspend", timeout.Token);
        AmqpDeliveryState? state = await again.SendAsync(new AmqpMessage { Body = new AmqpValueBody("after") }, cancellationToken: timeout.Token);

        // Assert: the host's view: the client's attach and its answer, the client's suspending detach and its
        // closing answer, then the client's attach again, its answer, and the two closing detaches.
        frames.ShouldBe(
        [
            "RECV attach", "SEND attach",
            "RECV detach", "SEND detach closed",
            "RECV attach", "SEND attach",
            "RECV detach closed", "SEND detach closed"
        ]);
        state.ShouldBeOfType<AmqpAccepted>();
        client.ConnectionClosed.IsCancellationRequested.ShouldBeFalse();
        session.SessionEnded.IsCancellationRequested.ShouldBeFalse();
    }

    private static ValueTask<IAmqpSender> AttachSenderAsync(IAmqpSession session, string address, CancellationToken cancellationToken)
    {
        return session.AttachSenderAsync(
            new AmqpSenderOptions
            {
                Name = address + "-sender",
                Source = new AmqpSource { Address = AmqpNetLiteHost.ClientContainerId },
                Target = new AmqpTarget { Address = address }
            },
            cancellationToken);
    }

    private static ValueTask<IAmqpReceiver> AttachReceiverAsync(IAmqpSession session, string address, uint prefetch, CancellationToken cancellationToken)
    {
        return session.AttachReceiverAsync(
            new AmqpReceiverOptions
            {
                Name = address + "-receiver",
                Source = new AmqpSource { Address = address },
                Target = new AmqpTarget { Address = AmqpNetLiteHost.ClientContainerId },
                Prefetch = prefetch
            },
            cancellationToken);
    }
}
