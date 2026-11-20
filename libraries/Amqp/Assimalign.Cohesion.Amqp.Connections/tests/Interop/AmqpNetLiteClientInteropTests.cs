using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Transactions;

using Shouldly;

using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Amqp;
using Assimalign.Cohesion.Connections;

using NetLite = Amqp;
using NetLiteFraming = Amqp.Framing;
using NetLiteTypes = Amqp.Types;

namespace Assimalign.Cohesion.Amqp.Connections.Tests;

/// <summary>
/// AMQPNetLite.Core as the client, Cohesion's listener as the broker, over TCP loopback (#432). Each test prints
/// the frames the peer sent and received; <c>docs/DESIGN.md</c>, "Interoperability", lists what the suite covers
/// and what the peer does not support.
/// </summary>
[Collection(AmqpInteropCollection.Name)]
[Trait("Category", AmqpInteropCollection.Category)]
public sealed class AmqpNetLiteClientInteropTests : IDisposable
{
    private static readonly TimeSpan _testTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan _peerTimeout = TimeSpan.FromSeconds(10);

    private readonly AmqpNetLiteTrace _trace;

    public AmqpNetLiteClientInteropTests(ITestOutputHelper output)
    {
        _trace = AmqpNetLiteTrace.Attach(output);
    }

    public void Dispose()
    {
        _trace.Dispose();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Interop AcceptAsync: Should open an AMQPNetLite client's connection over SASL PLAIN")]
    public async Task AcceptAsync_WithNetLiteClientOverSaslPlain_ShouldOpenBothEnds()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpInteropBroker broker = await AmqpInteropBroker.StartAsync(null, timeout.Token);

        // Act
        Task<IAmqpConnection> accept = broker.Listener.AcceptAsync(timeout.Token).AsTask();
        (NetLite.Connection peer, NetLiteFraming.Open remoteOpen) = await broker.ConnectPeerAsync(timeout.Token);
        await using IAmqpConnection server = await accept;

        // Assert
        IAmqpSaslConnectionInfo sasl = server.AmqpSasl.ShouldNotBeNull();
        sasl.Mechanism.ShouldBe(new AmqpSymbol("PLAIN"));
        sasl.Identity.ShouldBe(AmqpInteropBroker.UserName);
        server.State.ShouldBe(ConnectionState.Open);
        server.RemoteContainerId.ShouldBe(AmqpInteropBroker.PeerContainerId);
        server.RemoteHostName.ShouldBe(AmqpInteropBroker.HostName);
        remoteOpen.ContainerId.ShouldBe(AmqpInteropBroker.ContainerId);

        await peer.CloseAsync();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Interop AcceptAsync: Should open an AMQPNetLite client's connection over SASL ANONYMOUS")]
    public async Task AcceptAsync_WithNetLiteClientOverSaslAnonymous_ShouldOpenBothEnds()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpInteropBroker broker = await AmqpInteropBroker.StartAsync(null, timeout.Token);

        // Act
        Task<IAmqpConnection> accept = broker.Listener.AcceptAsync(timeout.Token).AsTask();
        (NetLite.Connection peer, _) = await broker.ConnectPeerAsync(timeout.Token, userName: null, password: null);
        await using IAmqpConnection server = await accept;

        // Assert
        server.AmqpSasl.ShouldNotBeNull().Mechanism.ShouldBe(new AmqpSymbol("ANONYMOUS"));
        server.State.ShouldBe(ConnectionState.Open);

        await peer.CloseAsync();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Interop AcceptAsync: Should refuse an AMQPNetLite client's wrong password and keep accepting")]
    public async Task AcceptAsync_WithWrongPasswordFromNetLite_ShouldRefuseTheLoginAndKeepAccepting()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpInteropBroker broker = await AmqpInteropBroker.StartAsync(null, timeout.Token);

        // Act: the first accept starts the listener's pump, so it is pending before either peer connects.
        Task<IAmqpConnection> accept = broker.Listener.AcceptAsync(timeout.Token).AsTask();
        NetLite.AmqpException refusal = await Should.ThrowAsync<NetLite.AmqpException>(
            () => broker.ConnectPeerAsync(timeout.Token, password: "wrong-password"));
        bool acceptedTheRefusedPeer = accept.IsCompleted;
        (NetLite.Connection peer, _) = await broker.ConnectPeerAsync(timeout.Token);
        await using IAmqpConnection server = await accept;

        // Assert
        refusal.Error.Condition.ToString().ShouldBe(NetLite.ErrorCode.UnauthorizedAccess);
        acceptedTheRefusedPeer.ShouldBeFalse();
        server.RemoteContainerId.ShouldBe(AmqpInteropBroker.PeerContainerId);

        await peer.CloseAsync();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Interop ReceiveAsync: Should settle an AMQPNetLite sender's transfers with each outcome")]
    public async Task ReceiveAsync_FromNetLiteSender_ShouldReportEachOutcomeToThePeer()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpInteropBroker broker = await AmqpInteropBroker.StartAsync(null, timeout.Token);
        (IAmqpConnection server, NetLite.Connection peer) = await ConnectAsync(broker, timeout.Token);
        await using IAmqpConnection serverConnection = server;
        Task<IAmqpReceiver> attach = AcceptReceiverAsync(server, timeout.Token);
        NetLite.SenderLink sender = new(new NetLite.Session(peer), "outcomes-sender", "outcomes");
        IAmqpReceiver receiver = await attach;
        AmqpSymbol retry = new("x-opt-retry");
        Func<IAmqpDelivery, ValueTask>[] settlements =
        [
            delivery => delivery.AcceptAsync(timeout.Token),
            delivery => delivery.RejectAsync(new AmqpError(AmqpErrorCondition.NotAllowed, "refused by the broker"), timeout.Token),
            delivery => delivery.ReleaseAsync(timeout.Token),
            delivery => delivery.ModifyAsync(
                deliveryFailed: true,
                undeliverableHere: true,
                messageAnnotations: new Dictionary<AmqpSymbol, object?> { [retry] = 1 },
                cancellationToken: timeout.Token)
        ];
        List<Task<NetLiteFraming.Outcome>> outcomes = [];

        // Act
        for (int index = 0; index < settlements.Length; index++)
        {
            outcomes.Add(SendWithOutcomeAsync(sender, new NetLite.Message($"message {index}")));
        }

        for (int index = 0; index < settlements.Length; index++)
        {
            IAmqpDelivery delivery = await receiver.ReceiveAsync(timeout.Token);

            delivery.GetMessage().Body.ShouldBeOfType<AmqpValueBody>().Value.ShouldBe($"message {index}");
            await settlements[index](delivery);
        }

        NetLiteFraming.Outcome[] settled = await Task.WhenAll(outcomes).WaitAsync(timeout.Token);

        // Assert
        settled[0].ShouldBeOfType<NetLiteFraming.Accepted>();
        NetLiteFraming.Error error = settled[1].ShouldBeOfType<NetLiteFraming.Rejected>().Error;
        error.Condition.ToString().ShouldBe(NetLite.ErrorCode.NotAllowed);
        error.Description.ShouldBe("refused by the broker");
        settled[2].ShouldBeOfType<NetLiteFraming.Released>();
        NetLiteFraming.Modified modified = settled[3].ShouldBeOfType<NetLiteFraming.Modified>();
        modified.DeliveryFailed.ShouldBeTrue();
        modified.UndeliverableHere.ShouldBeTrue();
        modified.MessageAnnotations[new NetLiteTypes.Symbol("x-opt-retry")].ShouldBe(1);

        await peer.CloseAsync();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Interop SendAsync: Should complete with each outcome an AMQPNetLite receiver settles with")]
    public async Task SendAsync_ToNetLiteReceiver_ShouldCompleteWithThePeersOutcome()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpInteropBroker broker = await AmqpInteropBroker.StartAsync(null, timeout.Token);
        (IAmqpConnection server, NetLite.Connection peer) = await ConnectAsync(broker, timeout.Token);
        await using IAmqpConnection serverConnection = server;
        Task<IAmqpSender> attach = AcceptSenderAsync(server, null, timeout.Token);
        NetLite.ReceiverLink peerReceiver = new(new NetLite.Session(peer), "outcomes-receiver", "outcomes");
        IAmqpSender sender = await attach;
        Action<NetLite.Message>[] settlements =
        [
            message => peerReceiver.Accept(message),
            message => peerReceiver.Reject(message, new NetLiteFraming.Error(NetLite.ErrorCode.NotAllowed) { Description = "refused by the peer" }),
            message => peerReceiver.Release(message),
            message => peerReceiver.Modify(message, true, true, new NetLiteTypes.Fields { [new NetLiteTypes.Symbol("x-opt-retry")] = 2 })
        ];
        List<Task<AmqpDeliveryState?>> sends = [];

        // Act
        peerReceiver.SetCredit(settlements.Length, autoRestore: false);

        for (int index = 0; index < settlements.Length; index++)
        {
            sends.Add(sender.SendAsync(new AmqpMessage { Body = new AmqpValueBody($"message {index}") }, cancellationToken: timeout.Token).AsTask());
        }

        for (int index = 0; index < settlements.Length; index++)
        {
            NetLite.Message message = (await peerReceiver.ReceiveAsync(_peerTimeout)).ShouldNotBeNull();

            message.Body.ShouldBe($"message {index}");
            settlements[index](message);
        }

        AmqpDeliveryState?[] states = await Task.WhenAll(sends).WaitAsync(timeout.Token);

        // Assert
        states[0].ShouldBeOfType<AmqpAccepted>();
        AmqpError error = states[1].ShouldBeOfType<AmqpRejected>().Error.ShouldNotBeNull();
        error.Condition.ShouldBe(AmqpErrorCondition.NotAllowed);
        error.Description.ShouldBe("refused by the peer");
        states[2].ShouldBeOfType<AmqpReleased>();
        AmqpModified modified = states[3].ShouldBeOfType<AmqpModified>();
        modified.DeliveryFailed.ShouldBeTrue();
        modified.UndeliverableHere.ShouldBeTrue();
        modified.MessageAnnotations.ShouldNotBeNull()[new AmqpSymbol("x-opt-retry")].ShouldBe(2);

        await peer.CloseAsync();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Interop ReceiveAsync: Should decode every type AMQPNetLite encodes and encode them back")]
    public async Task ReceiveAsync_EveryTypeFromNetLite_ShouldRoundTripThroughBothCodecs()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        DateTime timestamp = new(2026, 11, 20, 12, 30, 45, 123, DateTimeKind.Utc);
        NetLiteTypes.Map values = new()
        {
            ["null"] = null,
            ["boolean"] = true,
            ["ubyte"] = (byte)200,
            ["ushort"] = (ushort)60_000,
            ["uint"] = 4_000_000_000u,
            ["ulong"] = ulong.MaxValue,
            ["byte"] = (sbyte)-100,
            ["short"] = (short)-30_000,
            ["int"] = -2_000_000_000,
            ["long"] = long.MinValue,
            ["float"] = 1.5f,
            ["double"] = Math.PI,
            ["char"] = 'Ж',
            ["timestamp"] = timestamp,
            ["uuid"] = new Guid("6f9619ff-8b86-d011-b42d-00c04fc964ff"),
            ["binary"] = new byte[] { 0x00, 0x01, 0xFE, 0xFF },
            ["string"] = "Grüße, 世界",
            ["symbol"] = new NetLiteTypes.Symbol("interop:symbol"),
            // AMQPNetLite drops the trailing nulls of every list it encodes, so the null is not the last item.
            ["list"] = new NetLiteTypes.List { 1, null, "three" },
            ["map"] = new NetLiteTypes.Map { [new NetLiteTypes.Symbol("nested")] = 7L, [42] = "answer" },
            ["int-array"] = new[] { 1, 2, 3 },
            ["string-array"] = new[] { "x", "y" },
            ["symbol-array"] = new[] { new NetLiteTypes.Symbol("a"), new NetLiteTypes.Symbol("b") },
            ["described"] = new NetLiteTypes.DescribedValue(new NetLiteTypes.Symbol("interop:described"), "payload"),
            ["described-ulong"] = new NetLiteTypes.DescribedValue(0x0000_4141_0000_0001ul, 9)
        };
        await using AmqpInteropBroker broker = await AmqpInteropBroker.StartAsync(null, timeout.Token);
        (IAmqpConnection server, NetLite.Connection peer) = await ConnectAsync(broker, timeout.Token);
        await using IAmqpConnection serverConnection = server;
        IAmqpSession serverSession = await AcceptSessionWithPeerAsync(server, peer, out NetLite.Session peerSession, timeout.Token);
        Task<IAmqpReceiver> acceptReceiver = AcceptReceiverAsync(serverSession, timeout.Token);
        NetLite.SenderLink peerSender = new(peerSession, "types-sender", "types");
        IAmqpReceiver receiver = await acceptReceiver;
        Task<IAmqpSender> acceptSender = AcceptSenderAsync(serverSession, null, timeout.Token);
        NetLite.ReceiverLink peerReceiver = new(peerSession, "types-echo", "types-echo");
        IAmqpSender echo = await acceptSender;

        // Act
        Task sent = peerSender.SendAsync(new NetLite.Message(values));
        IAmqpDelivery delivery = await ReceiveFromPeerAsync(receiver, sent, timeout.Token);
        Dictionary<object, object?> decoded = delivery.GetMessage().Body.ShouldBeOfType<AmqpValueBody>().Value
            .ShouldBeOfType<Dictionary<object, object?>>();
        await delivery.AcceptAsync(timeout.Token);
        await sent.WaitAsync(timeout.Token);
        peerReceiver.SetCredit(1, autoRestore: false);
        Task<AmqpDeliveryState?> echoed = echo.SendAsync(new AmqpMessage { Body = new AmqpValueBody(decoded) }, cancellationToken: timeout.Token).AsTask();
        NetLite.Message returned = (await peerReceiver.ReceiveAsync(_peerTimeout)).ShouldNotBeNull();
        peerReceiver.Accept(returned);
        await echoed;

        // Assert: what Cohesion decoded is the type the specification maps each encoding to.
        decoded.Count.ShouldBe(values.Count);
        decoded["null"].ShouldBeNull();
        decoded["ubyte"].ShouldBe((byte)200);
        decoded["byte"].ShouldBe((sbyte)-100);
        decoded["ulong"].ShouldBe(ulong.MaxValue);
        decoded["long"].ShouldBe(long.MinValue);
        decoded["double"].ShouldBe(Math.PI);
        decoded["char"].ShouldBe(new Rune('Ж'));
        decoded["timestamp"].ShouldBe(new DateTimeOffset(timestamp));
        decoded["uuid"].ShouldBe(new Guid("6f9619ff-8b86-d011-b42d-00c04fc964ff"));
        decoded["binary"].ShouldBeOfType<byte[]>().ShouldBe(new byte[] { 0x00, 0x01, 0xFE, 0xFF });
        decoded["string"].ShouldBe("Grüße, 世界");
        decoded["symbol"].ShouldBe(new AmqpSymbol("interop:symbol"));
        decoded["list"].ShouldBeOfType<List<object?>>().ShouldBe(new List<object?> { 1, null, "three" });
        Dictionary<object, object?> nested = decoded["map"].ShouldBeOfType<Dictionary<object, object?>>();
        nested[new AmqpSymbol("nested")].ShouldBe(7L);
        nested[42].ShouldBe("answer");
        decoded["int-array"].ShouldBeOfType<int[]>().ShouldBe(new[] { 1, 2, 3 });
        decoded["symbol-array"].ShouldBeOfType<AmqpSymbol[]>().ShouldBe(new[] { new AmqpSymbol("a"), new AmqpSymbol("b") });
        AmqpDescribedValue described = decoded["described"].ShouldBeOfType<AmqpDescribedValue>();
        described.Descriptor.ShouldBe(new AmqpSymbol("interop:described"));
        described.Value.ShouldBe("payload");
        decoded["described-ulong"].ShouldBeOfType<AmqpDescribedValue>().Descriptor.ShouldBe(0x0000_4141_0000_0001ul);

        // Assert: what Cohesion encoded back, AMQPNetLite decodes to the values it sent.
        NetLiteTypes.Map roundTripped = returned.Body.ShouldBeOfType<NetLiteTypes.Map>();
        roundTripped.Count.ShouldBe(values.Count);

        foreach (object key in values.Keys)
        {
            AmqpNetLiteValues.AreEqual(values[key], roundTripped[key]).ShouldBeTrue($"the {key} value to come back as it was sent");
        }

        await peer.CloseAsync();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Interop ReceiveAsync: Should decode every section of an AMQPNetLite message")]
    public async Task ReceiveAsync_EveryMessageSectionFromNetLite_ShouldArriveAsSent()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        DateTime created = new(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);
        Guid correlationId = new("0f8fad5b-d9cb-469f-a165-70867728950e");
        byte[] userId = Encoding.UTF8.GetBytes(AmqpInteropBroker.UserName);
        NetLiteFraming.Properties properties = new()
        {
            UserId = userId,
            To = "orders",
            Subject = "created",
            ReplyTo = "replies",
            ContentType = new NetLiteTypes.Symbol("application/octet-stream"),
            ContentEncoding = new NetLiteTypes.Symbol("identity"),
            AbsoluteExpiryTime = created.AddMinutes(1),
            CreationTime = created,
            GroupId = "group",
            GroupSequence = 9,
            ReplyToGroupId = "reply-group"
        };
        properties.SetMessageId(42UL);
        properties.SetCorrelationId(correlationId);
        NetLiteFraming.DeliveryAnnotations deliveryAnnotations = new();
        deliveryAnnotations[new NetLiteTypes.Symbol("x-opt-trace")] = "interop";
        NetLiteFraming.MessageAnnotations messageAnnotations = new();
        messageAnnotations[new NetLiteTypes.Symbol("x-opt-partition")] = 3;
        NetLiteFraming.ApplicationProperties applicationProperties = new();
        applicationProperties["region"] = "us-east";
        applicationProperties["attempt"] = 2;
        NetLiteFraming.Footer footer = new();
        footer[new NetLiteTypes.Symbol("x-opt-checksum")] = 0xCAFEu;
        NetLiteFraming.DataList data = new();
        data.Add(new NetLiteFraming.Data { Binary = [1, 2] });
        data.Add(new NetLiteFraming.Data { Binary = [3] });
        NetLite.Message message = new()
        {
            Header = new NetLiteFraming.Header { Durable = true, Priority = 7, Ttl = 60_000, FirstAcquirer = true, DeliveryCount = 3 },
            DeliveryAnnotations = deliveryAnnotations,
            MessageAnnotations = messageAnnotations,
            Properties = properties,
            ApplicationProperties = applicationProperties,
            BodySection = data,
            Footer = footer
        };
        await using AmqpInteropBroker broker = await AmqpInteropBroker.StartAsync(null, timeout.Token);
        (IAmqpConnection server, NetLite.Connection peer) = await ConnectAsync(broker, timeout.Token);
        await using IAmqpConnection serverConnection = server;
        Task<IAmqpReceiver> attach = AcceptReceiverAsync(server, timeout.Token);
        NetLite.SenderLink sender = new(new NetLite.Session(peer), "sections-sender", "sections");
        IAmqpReceiver receiver = await attach;

        // Act
        Task sent = sender.SendAsync(message);
        IAmqpDelivery delivery = await ReceiveFromPeerAsync(receiver, sent, timeout.Token);
        AmqpMessage received = delivery.GetMessage();
        await delivery.AcceptAsync(timeout.Token);
        await sent.WaitAsync(timeout.Token);

        // Assert
        AmqpHeader header = received.Header.ShouldNotBeNull();
        header.Durable.ShouldBeTrue();
        header.Priority.ShouldBe((byte)7);
        header.TimeToLive.ShouldBe(60_000u);
        header.FirstAcquirer.ShouldBeTrue();
        header.DeliveryCount.ShouldBe(3u);
        received.DeliveryAnnotations.ShouldNotBeNull()[new AmqpSymbol("x-opt-trace")].ShouldBe("interop");
        received.MessageAnnotations.ShouldNotBeNull()[new AmqpSymbol("x-opt-partition")].ShouldBe(3);
        AmqpProperties arrived = received.Properties.ShouldNotBeNull();
        arrived.MessageId.ShouldBe(42UL);
        arrived.UserId.ShouldNotBeNull().ToArray().ShouldBe(userId);
        arrived.To.ShouldBe("orders");
        arrived.Subject.ShouldBe("created");
        arrived.ReplyTo.ShouldBe("replies");
        arrived.CorrelationId.ShouldBe(correlationId);
        arrived.ContentType.ShouldBe(new AmqpSymbol("application/octet-stream"));
        arrived.ContentEncoding.ShouldBe(new AmqpSymbol("identity"));
        arrived.AbsoluteExpiryTime.ShouldBe(new DateTimeOffset(created.AddMinutes(1)));
        arrived.CreationTime.ShouldBe(new DateTimeOffset(created));
        arrived.GroupId.ShouldBe("group");
        arrived.GroupSequence.ShouldBe(9u);
        arrived.ReplyToGroupId.ShouldBe("reply-group");
        IReadOnlyDictionary<string, object?> application = received.ApplicationProperties.ShouldNotBeNull();
        application["region"].ShouldBe("us-east");
        application["attempt"].ShouldBe(2);
        AmqpDataBody body = received.Body.ShouldBeOfType<AmqpDataBody>();
        body.Sections.Count.ShouldBe(2);
        body.Sections[0].ToArray().ShouldBe(new byte[] { 1, 2 });
        body.Sections[1].ToArray().ShouldBe(new byte[] { 3 });
        received.Footer.ShouldNotBeNull()[new AmqpSymbol("x-opt-checksum")].ShouldBe(0xCAFEu);

        await peer.CloseAsync();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Interop SendAsync: Should encode every section so AMQPNetLite decodes it as sent")]
    public async Task SendAsync_EveryMessageSectionToNetLite_ShouldArriveAsSent()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        DateTimeOffset created = DateTimeOffset.FromUnixTimeMilliseconds(1_763_600_000_000);
        AmqpMessage message = new()
        {
            Header = new AmqpHeader { Durable = true, Priority = 6, TimeToLive = 30_000, FirstAcquirer = true, DeliveryCount = 2 },
            DeliveryAnnotations = new Dictionary<AmqpSymbol, object?> { [new AmqpSymbol("x-opt-trace")] = "cohesion" },
            MessageAnnotations = new Dictionary<AmqpSymbol, object?> { [new AmqpSymbol("x-opt-partition")] = 5 },
            Properties = new AmqpProperties
            {
                MessageId = "cohesion-7",
                UserId = new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes("cohesion")),
                To = "replies",
                Subject = "shipped",
                ReplyTo = "orders",
                CorrelationId = 99UL,
                ContentType = new AmqpSymbol("application/json"),
                ContentEncoding = new AmqpSymbol("utf-8"),
                AbsoluteExpiryTime = created.AddHours(1),
                CreationTime = created,
                GroupId = "group-2",
                GroupSequence = 4,
                ReplyToGroupId = "reply-group-2"
            },
            ApplicationProperties = new Dictionary<string, object?> { ["region"] = "eu-west", ["attempt"] = 1L },
            Body = new AmqpSequenceBody([new List<object?> { 1, "a", true }]),
            Footer = new Dictionary<AmqpSymbol, object?> { [new AmqpSymbol("x-opt-checksum")] = 0xBEEFu }
        };
        await using AmqpInteropBroker broker = await AmqpInteropBroker.StartAsync(null, timeout.Token);
        (IAmqpConnection server, NetLite.Connection peer) = await ConnectAsync(broker, timeout.Token);
        await using IAmqpConnection serverConnection = server;
        Task<IAmqpSender> attach = AcceptSenderAsync(server, null, timeout.Token);
        NetLite.ReceiverLink peerReceiver = new(new NetLite.Session(peer), "sections-receiver", "sections");
        IAmqpSender sender = await attach;

        // Act
        peerReceiver.SetCredit(1, autoRestore: false);
        Task<AmqpDeliveryState?> send = sender.SendAsync(message, cancellationToken: timeout.Token).AsTask();
        NetLite.Message received = (await peerReceiver.ReceiveAsync(_peerTimeout)).ShouldNotBeNull();
        peerReceiver.Accept(received);
        AmqpDeliveryState? state = await send;

        // Assert
        state.ShouldBeOfType<AmqpAccepted>();
        received.Header.Durable.ShouldBeTrue();
        received.Header.Priority.ShouldBe((byte)6);
        received.Header.Ttl.ShouldBe(30_000u);
        received.Header.FirstAcquirer.ShouldBeTrue();
        received.Header.DeliveryCount.ShouldBe(2u);
        received.DeliveryAnnotations[new NetLiteTypes.Symbol("x-opt-trace")].ShouldBe("cohesion");
        received.MessageAnnotations[new NetLiteTypes.Symbol("x-opt-partition")].ShouldBe(5);
        received.Properties.GetMessageId().ShouldBe("cohesion-7");
        received.Properties.UserId.ShouldBe(Encoding.UTF8.GetBytes("cohesion"));
        received.Properties.To.ShouldBe("replies");
        received.Properties.Subject.ShouldBe("shipped");
        received.Properties.ReplyTo.ShouldBe("orders");
        received.Properties.GetCorrelationId().ShouldBe(99UL);
        received.Properties.ContentType.ToString().ShouldBe("application/json");
        received.Properties.ContentEncoding.ToString().ShouldBe("utf-8");
        received.Properties.AbsoluteExpiryTime.ShouldBe(created.AddHours(1).UtcDateTime);
        received.Properties.CreationTime.ShouldBe(created.UtcDateTime);
        received.Properties.GroupId.ShouldBe("group-2");
        received.Properties.GroupSequence.ShouldBe(4u);
        received.Properties.ReplyToGroupId.ShouldBe("reply-group-2");
        received.ApplicationProperties["region"].ShouldBe("eu-west");
        received.ApplicationProperties["attempt"].ShouldBe(1L);
        NetLiteFraming.AmqpSequence sequence = received.BodySection.ShouldBeOfType<NetLiteFraming.AmqpSequence>();
        AmqpNetLiteValues.AreEqual(new NetLiteTypes.List { 1, "a", true }, sequence.List).ShouldBeTrue("the sequence to arrive as it was sent");
        received.Footer[new NetLiteTypes.Symbol("x-opt-checksum")].ShouldBe(0xBEEFu);

        await peer.CloseAsync();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Interop ReceiveAsync: Should take a settled AMQPNetLite transfer as delivered at most once")]
    public async Task ReceiveAsync_PreSettledFromNetLite_ShouldArriveSettled()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpInteropBroker broker = await AmqpInteropBroker.StartAsync(null, timeout.Token);
        (IAmqpConnection server, NetLite.Connection peer) = await ConnectAsync(broker, timeout.Token);
        await using IAmqpConnection serverConnection = server;
        Task<IAmqpReceiver> attach = AcceptReceiverAsync(server, timeout.Token);
        NetLite.SenderLink sender = new(
            new NetLite.Session(peer),
            "settled-sender",
            new NetLiteFraming.Attach
            {
                Source = new NetLiteFraming.Source(),
                Target = new NetLiteFraming.Target { Address = "settled" },
                SndSettleMode = NetLiteFraming.SenderSettleMode.Settled
            },
            null);
        IAmqpReceiver receiver = await attach;

        // Act
        await sender.SendAsync(new NetLite.Message(17)).WaitAsync(timeout.Token);
        IAmqpDelivery delivery = await receiver.ReceiveAsync(timeout.Token);

        // Assert
        receiver.SenderSettleMode.ShouldBe(AmqpSenderSettleMode.Settled);
        delivery.IsSettled.ShouldBeTrue();
        delivery.GetMessage().Body.ShouldBeOfType<AmqpValueBody>().Value.ShouldBe(17);

        await peer.CloseAsync();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Interop SendAsync: Should deliver a settled transfer to AMQPNetLite without an outcome")]
    public async Task SendAsync_PreSettledToNetLite_ShouldCompleteWithoutAnOutcome()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpInteropBroker broker = await AmqpInteropBroker.StartAsync(null, timeout.Token);
        (IAmqpConnection server, NetLite.Connection peer) = await ConnectAsync(broker, timeout.Token);
        await using IAmqpConnection serverConnection = server;
        Task<IAmqpSender> attach = AcceptSenderAsync(
            server,
            new AmqpSenderOptions { SenderSettleMode = AmqpSenderSettleMode.Settled },
            timeout.Token);
        NetLite.ReceiverLink peerReceiver = new(new NetLite.Session(peer), "settled-receiver", "settled");
        IAmqpSender sender = await attach;

        // Act
        peerReceiver.SetCredit(1, autoRestore: false);
        AmqpDeliveryState? state = await sender.SendAsync(new AmqpMessage { Body = new AmqpValueBody(17) }, cancellationToken: timeout.Token);
        NetLite.Message received = (await peerReceiver.ReceiveAsync(_peerTimeout)).ShouldNotBeNull();

        // Assert
        state.ShouldBeNull();
        received.Body.ShouldBe(17);

        await peer.CloseAsync();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Interop ReceiveAsync: Should reassemble an AMQPNetLite message split across frames")]
    public async Task ReceiveAsync_LargerThanTheFrameFromNetLite_ShouldReassembleIt()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        byte[] payload = CreatePayload(256 * 1024);
        await using AmqpInteropBroker broker = await AmqpInteropBroker.StartAsync(options => options.MaxFrameSize = 4096, timeout.Token);
        (IAmqpConnection server, NetLite.Connection peer) = await ConnectAsync(broker, timeout.Token, factory => factory.AMQP.MaxFrameSize = 4096);
        await using IAmqpConnection serverConnection = server;
        Task<IAmqpReceiver> attach = AcceptReceiverAsync(server, timeout.Token);
        NetLite.SenderLink sender = new(new NetLite.Session(peer), "split-sender", "split");
        IAmqpReceiver receiver = await attach;

        // Act
        Task sent = sender.SendAsync(new NetLite.Message { BodySection = new NetLiteFraming.Data { Binary = payload } });
        IAmqpDelivery delivery = await ReceiveFromPeerAsync(receiver, sent, timeout.Token);
        AmqpMessage received = delivery.GetMessage();
        await delivery.AcceptAsync(timeout.Token);
        await sent.WaitAsync(timeout.Token);

        // Assert
        server.MaxFrameSize.ShouldBe(4096u);
        AmqpDataBody body = received.Body.ShouldBeOfType<AmqpDataBody>();
        body.Sections.Count.ShouldBe(1);
        body.Sections[0].Span.SequenceEqual(payload).ShouldBeTrue("the body to arrive byte for byte");

        await peer.CloseAsync();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Interop SendAsync: Should split a message across frames so AMQPNetLite reassembles it")]
    public async Task SendAsync_LargerThanTheFrameToNetLite_ShouldReassembleIt()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        byte[] payload = CreatePayload(256 * 1024);
        await using AmqpInteropBroker broker = await AmqpInteropBroker.StartAsync(options => options.MaxFrameSize = 4096, timeout.Token);
        (IAmqpConnection server, NetLite.Connection peer) = await ConnectAsync(broker, timeout.Token, factory => factory.AMQP.MaxFrameSize = 4096);
        await using IAmqpConnection serverConnection = server;
        Task<IAmqpSender> attach = AcceptSenderAsync(server, null, timeout.Token);
        NetLite.ReceiverLink peerReceiver = new(new NetLite.Session(peer), "split-receiver", "split");
        IAmqpSender sender = await attach;

        // Act
        peerReceiver.SetCredit(1, autoRestore: false);
        Task<AmqpDeliveryState?> send = sender.SendAsync(new AmqpMessage { Body = new AmqpDataBody(payload) }, cancellationToken: timeout.Token).AsTask();
        NetLite.Message received = (await peerReceiver.ReceiveAsync(_peerTimeout)).ShouldNotBeNull();
        peerReceiver.Accept(received);
        AmqpDeliveryState? state = await send;

        // Assert
        state.ShouldBeOfType<AmqpAccepted>();
        received.Body.ShouldBeOfType<byte[]>().AsSpan().SequenceEqual(payload).ShouldBeTrue("the body to arrive byte for byte");

        await peer.CloseAsync();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Interop ReceiveAsync: Should keep each link's order across AMQPNetLite sessions and links")]
    public async Task ReceiveAsync_FromManyNetLiteSessionsAndLinks_ShouldKeepEachLinksOrder()
    {
        // Arrange
        const int sessions = 2;
        const int linksPerSession = 2;
        const int messagesPerLink = 10;
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpInteropBroker broker = await AmqpInteropBroker.StartAsync(null, timeout.Token);
        (IAmqpConnection server, NetLite.Connection peer) = await ConnectAsync(broker, timeout.Token);
        await using IAmqpConnection serverConnection = server;
        Task<Dictionary<string, List<string>>> collect = CollectAsync(server, sessions, linksPerSession, messagesPerLink, timeout.Token);
        List<Task> sends = [];

        // Act
        for (int session = 0; session < sessions; session++)
        {
            NetLite.Session peerSession = new(peer);

            for (int link = 0; link < linksPerSession; link++)
            {
                string name = $"session-{session}-link-{link}";
                NetLite.SenderLink sender = new(peerSession, name, name);

                for (int index = 0; index < messagesPerLink; index++)
                {
                    sends.Add(sender.SendAsync(new NetLite.Message($"{name}:{index}")));
                }
            }
        }

        await Task.WhenAll(sends).WaitAsync(timeout.Token);
        Dictionary<string, List<string>> received = await collect;

        // Assert
        received.Count.ShouldBe(sessions * linksPerSession);

        foreach ((string name, List<string> bodies) in received)
        {
            bodies.ShouldBe(Enumerable.Range(0, messagesPerLink).Select(index => $"{name}:{index}").ToList());
        }

        await peer.CloseAsync();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Interop AcceptLinkAsync: Should fail a refused AMQPNetLite link with the broker's error")]
    public async Task RejectAsync_ForNetLiteLink_ShouldFailThePeersLinkWithTheError()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpInteropBroker broker = await AmqpInteropBroker.StartAsync(null, timeout.Token);
        (IAmqpConnection server, NetLite.Connection peer) = await ConnectAsync(broker, timeout.Token);
        await using IAmqpConnection serverConnection = server;
        Task<IAmqpSession> acceptSession = server.AcceptSessionAsync(timeout.Token).AsTask();
        TaskCompletionSource<NetLiteFraming.Error?> closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        NetLite.SenderLink sender = new(new NetLite.Session(peer), "missing-sender", "missing");
        sender.AddClosedCallback((_, error) => closed.TrySetResult(error));
        IAmqpSession session = await acceptSession;

        // Act
        IAmqpLinkRequest request = await session.AcceptLinkAsync(timeout.Token);
        await request.RejectAsync(new AmqpError(AmqpErrorCondition.NotFound, "no such node"), timeout.Token);
        NetLiteFraming.Error? error = await closed.Task.WaitAsync(timeout.Token);

        // Assert
        request.Name.ShouldBe("missing-sender");
        error.ShouldNotBeNull();
        error.Condition.ToString().ShouldBe(NetLite.ErrorCode.NotFound);
        error.Description.ShouldBe("no such node");

        await peer.CloseAsync();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Interop CloseAsync: Should give AMQPNetLite the error the broker closed with")]
    public async Task CloseAsync_WithErrorToNetLite_ShouldReachThePeer()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpInteropBroker broker = await AmqpInteropBroker.StartAsync(null, timeout.Token);
        (IAmqpConnection server, NetLite.Connection peer) = await ConnectAsync(broker, timeout.Token);
        await using IAmqpConnection serverConnection = server;
        TaskCompletionSource<NetLiteFraming.Error?> closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        peer.AddClosedCallback((_, error) => closed.TrySetResult(error));

        // Act
        await server.CloseAsync(new AmqpError(AmqpErrorCondition.ConnectionForced, "maintenance"), timeout.Token);
        NetLiteFraming.Error? error = await closed.Task.WaitAsync(timeout.Token);

        // Assert
        error.ShouldNotBeNull();
        error.Condition.ToString().ShouldBe(NetLite.ErrorCode.ConnectionForced);
        error.Description.ShouldBe("maintenance");
        server.ConnectionClosed.IsCancellationRequested.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Interop ConnectionClosed: Should end the broker's connection when AMQPNetLite closes with an error")]
    public async Task ConnectionClosed_WhenNetLiteClosesWithError_ShouldEndTheServerConnection()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpInteropBroker broker = await AmqpInteropBroker.StartAsync(null, timeout.Token);
        AmqpNetLiteConnectionEvents events = new();
        Task<IAmqpConnection> accept = broker.Listener.AcceptAsync(timeout.Token).AsTask();
        (NetLite.Connection peer, _) = await broker.ConnectPeerAsync(timeout.Token, events: events);
        await using IAmqpConnection server = await accept;
        NetLiteFraming.Error shutdown = new(NetLite.ErrorCode.InternalError) { Description = "client shutting down" };

        // Act
        try
        {
            await peer.CloseAsync(_peerTimeout, shutdown);
        }
        catch (NetLite.AmqpException exception) when (exception.Error == shutdown)
        {
            // AMQPNetLite reports the error it closed with, whatever the answer was: the answer is the close below.
        }

        NetLiteFraming.Close answer = await events.RemoteClose.WaitAsync(timeout.Token);
        await WaitForAsync(server.ConnectionClosed, timeout.Token);
        AmqpException closed = await Should.ThrowAsync<AmqpException>(server.BeginSessionAsync(cancellationToken: timeout.Token).AsTask());

        // Assert
        answer.Error.ShouldBeNull();
        closed.ErrorCode.ShouldBe(AmqpErrorCode.ConnectionClosed);
        AmqpError error = closed.Error.ShouldNotBeNull();
        error.Condition.ShouldBe(AmqpErrorCondition.InternalError);
        error.Description.ShouldBe("client shutting down");
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Interop Transactions: Should commit an AMQPNetLite transaction through the broker's coordinator")]
    public async Task Coordinator_WithNetLiteTransactionScopeCommitted_ShouldCommitThePostedTransfer()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        TestTransactionHandler handler = new();
        await using AmqpInteropBroker broker = await AmqpInteropBroker.StartAsync(null, timeout.Token);
        (IAmqpConnection server, NetLite.Connection peer) = await ConnectAsync(broker, timeout.Token);
        await using IAmqpConnection serverConnection = server;
        IAmqpSession serverSession = await AcceptSessionWithPeerAsync(server, peer, out NetLite.Session peerSession, timeout.Token);
        Task<IAmqpReceiver> acceptReceiver = AcceptReceiverAsync(serverSession, timeout.Token);
        NetLite.SenderLink sender = new(peerSession, "transactional-sender", "transactional");
        IAmqpReceiver receiver = await acceptReceiver;
        Task<AmqpTransactionCoordinator> acceptCoordinator = AcceptCoordinatorAsync(server, handler, timeout.Token);
        Task<IAmqpDelivery> receive = receiver.ReceiveAsync(timeout.Token).AsTask();

        // Act: the commit must not block. AMQPNetLite completes a send inline, on the thread reading the frame that
        // settled it, so the code after the await below runs there; a blocking commit (TransactionScope.Dispose)
        // would hold the only reader of the discharge outcome it waits for.
        using CommittableTransaction transaction = new();
        Task posted;
        AmqpTransactionCoordinator coordinator;
        IAmqpDelivery delivery;

        using (TransactionScope scope = new(transaction, TransactionScopeAsyncFlowOption.Enabled))
        {
            posted = sender.SendAsync(new NetLite.Message("posted in a transaction"));
            coordinator = await acceptCoordinator;
            _ = coordinator.RunAsync(timeout.Token);
            delivery = await receive;
            AmqpTransactionalState state = delivery.RemoteState.ShouldBeOfType<AmqpTransactionalState>();
            await delivery.SettleAsync(new AmqpTransactionalState(state.TransactionId, AmqpAccepted.Instance), timeout.Token);
            await posted.WaitAsync(timeout.Token);
            scope.Complete();
        }

        await Task.Factory.FromAsync(transaction.BeginCommit, transaction.EndCommit, null).WaitAsync(timeout.Token);
        await handler.Committed.WaitAsync(timeout.Token);

        // Assert
        handler.Declares.Count.ShouldBe(1);
        IReadOnlyList<(byte[] TransactionId, bool Fail)> discharges = handler.Discharges;
        discharges.Count.ShouldBe(1);
        discharges[0].Fail.ShouldBeFalse();
        discharges[0].TransactionId.ShouldBe(((AmqpTransactionalState)delivery.RemoteState!).TransactionId.ToArray());
        coordinator.GetActiveTransactions().Count.ShouldBe(0);

        await peer.CloseAsync();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Interop Transactions: Should roll back an AMQPNetLite transaction the scope abandons")]
    public async Task Coordinator_WithNetLiteTransactionScopeAbandoned_ShouldRollBack()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        TestTransactionHandler handler = new();
        await using AmqpInteropBroker broker = await AmqpInteropBroker.StartAsync(null, timeout.Token);
        (IAmqpConnection server, NetLite.Connection peer) = await ConnectAsync(broker, timeout.Token);
        await using IAmqpConnection serverConnection = server;
        IAmqpSession serverSession = await AcceptSessionWithPeerAsync(server, peer, out NetLite.Session peerSession, timeout.Token);
        Task<IAmqpReceiver> acceptReceiver = AcceptReceiverAsync(serverSession, timeout.Token);
        NetLite.SenderLink sender = new(peerSession, "abandoned-sender", "abandoned");
        IAmqpReceiver receiver = await acceptReceiver;
        Task<AmqpTransactionCoordinator> acceptCoordinator = AcceptCoordinatorAsync(server, handler, timeout.Token);
        Task<IAmqpDelivery> receive = receiver.ReceiveAsync(timeout.Token).AsTask();

        // Act
        using (TransactionScope scope = new(TransactionScopeAsyncFlowOption.Enabled))
        {
            Task posted = sender.SendAsync(new NetLite.Message("posted, then abandoned"));
            AmqpTransactionCoordinator coordinator = await acceptCoordinator;
            _ = coordinator.RunAsync(timeout.Token);
            IAmqpDelivery delivery = await receive;
            AmqpTransactionalState state = delivery.RemoteState.ShouldBeOfType<AmqpTransactionalState>();
            await delivery.SettleAsync(new AmqpTransactionalState(state.TransactionId, AmqpAccepted.Instance), timeout.Token);
            await posted.WaitAsync(timeout.Token);
        }

        await handler.RolledBack.WaitAsync(timeout.Token);

        // Assert
        IReadOnlyList<(byte[] TransactionId, bool Fail)> discharges = handler.Discharges;
        discharges.Count.ShouldBe(1);
        discharges[0].Fail.ShouldBeTrue();

        await peer.CloseAsync();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Interop Drain: Should end an AMQPNetLite receiver's drain and serve its next one once it leaves drain mode")]
    public async Task Drain_AskedAgainByNetLiteReceiver_ShouldServeEachCycle()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpInteropBroker broker = await AmqpInteropBroker.StartAsync(null, timeout.Token);
        (IAmqpConnection server, NetLite.Connection peer) = await ConnectAsync(broker, timeout.Token);
        await using IAmqpConnection serverConnection = server;
        IAmqpSession serverSession = await AcceptSessionWithPeerAsync(server, peer, out NetLite.Session peerSession, timeout.Token);
        Task<IAmqpSender> acceptDrained = AcceptSenderAsync(serverSession, null, timeout.Token);
        NetLite.ReceiverLink peerReceiver = new(peerSession, "drain-receiver", "drain");
        IAmqpSender sender = await acceptDrained;
        Task<IAmqpSender> acceptBarrier = AcceptSenderAsync(serverSession, null, timeout.Token);
        NetLite.ReceiverLink peerBarrier = new(peerSession, "barrier-receiver", "barrier");
        IAmqpSender barrier = await acceptBarrier;

        // Act: the first drain finds nothing to send, so the broker uses up the credit. The peer reads frames in
        // order, so once it holds a message the broker sent after that, it has read the flow that ended the drain.
        peerReceiver.SetCredit(5, NetLite.CreditMode.Drain);
        peerBarrier.SetCredit(1, autoRestore: false);
        Task<AmqpDeliveryState?> barrierSent = barrier.SendAsync(new AmqpMessage { Body = new AmqpValueBody("barrier") }, cancellationToken: timeout.Token).AsTask();
        NetLite.Message barrierMessage = (await peerBarrier.ReceiveAsync(_peerTimeout)).ShouldNotBeNull();
        peerBarrier.Accept(barrierMessage);
        await barrierSent;
        uint creditAfterFirstDrain = sender.Credit;

        // The broker's flow keeps the drain flag the receiver set (Part 2, 2.6.7; rule F18), and AMQPNetLite stays in
        // drain mode until a flow clears the flag or deliveries use up its credit, ignoring a new drain until then.
        // A manual grant of nothing takes it out of drain mode (docs/DESIGN.md, "Interoperability"). The send waits
        // before the next drain arrives, so the broker sends rather than giving the credit back.
        peerReceiver.SetCredit(0, NetLite.CreditMode.Manual);
        Task<AmqpDeliveryState?> sent = sender.SendAsync(new AmqpMessage { Body = new AmqpValueBody("after the first drain") }, cancellationToken: timeout.Token).AsTask();
        peerReceiver.SetCredit(1, NetLite.CreditMode.Drain);
        NetLite.Message? received = await peerReceiver.ReceiveAsync(_peerTimeout);

        // Assert
        creditAfterFirstDrain.ShouldBe(0u);
        received.ShouldNotBeNull("the second drain reached the broker once AMQPNetLite left drain mode");
        received.Body.ShouldBe("after the first drain");
        peerReceiver.Accept(received);
        (await sent.WaitAsync(timeout.Token)).ShouldBeOfType<AmqpAccepted>();

        await peer.CloseAsync();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Interop IdleTimeout: Should keep an idle connection to AMQPNetLite open with empty frames")]
    public async Task IdleTimeout_DeclaredByBothEnds_ShouldKeepAnIdleConnectionOpen()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpInteropBroker broker = await AmqpInteropBroker.StartAsync(options => options.IdleTimeout = TimeSpan.FromSeconds(1), timeout.Token);
        (IAmqpConnection server, NetLite.Connection peer) = await ConnectAsync(broker, timeout.Token, factory => factory.AMQP.IdleTimeout = 1000);
        await using IAmqpConnection serverConnection = server;
        Task<IAmqpReceiver> attach = AcceptReceiverAsync(server, timeout.Token);
        NetLite.SenderLink sender = new(new NetLite.Session(peer), "idle-sender", "idle");
        IAmqpReceiver receiver = await attach;

        // Act: three times each side's timeout, with nothing to send but empty frames.
        await Task.Delay(TimeSpan.FromSeconds(3), timeout.Token);
        Task sent = sender.SendAsync(new NetLite.Message("still here"));
        IAmqpDelivery delivery = await ReceiveFromPeerAsync(receiver, sent, timeout.Token);
        await delivery.AcceptAsync(timeout.Token);
        await sent.WaitAsync(timeout.Token);

        // Assert: each end advertises half its own timeout (Part 2, 2.4.5), so a 1 s timeout reads as 500 ms.
        server.RemoteIdleTimeout.ShouldBe(TimeSpan.FromMilliseconds(500));
        server.State.ShouldBe(ConnectionState.Open);
        peer.IsClosed.ShouldBeFalse();
        delivery.GetMessage().Body.ShouldBeOfType<AmqpValueBody>().Value.ShouldBe("still here");

        await peer.CloseAsync();
    }

    private static byte[] CreatePayload(int length)
    {
        byte[] payload = new byte[length];

        for (int index = 0; index < payload.Length; index++)
        {
            payload[index] = (byte)((index * 31) + 7);
        }

        return payload;
    }

    private static async Task<(IAmqpConnection Server, NetLite.Connection Peer)> ConnectAsync(
        AmqpInteropBroker broker,
        CancellationToken cancellationToken,
        Action<NetLite.ConnectionFactory>? configure = null)
    {
        Task<IAmqpConnection> accept = broker.Listener.AcceptAsync(cancellationToken).AsTask();
        (NetLite.Connection peer, _) = await broker.ConnectPeerAsync(cancellationToken, configure: configure);

        return (await accept, peer);
    }

    private static Task<IAmqpSession> AcceptSessionWithPeerAsync(
        IAmqpConnection server,
        NetLite.Connection peer,
        out NetLite.Session peerSession,
        CancellationToken cancellationToken)
    {
        Task<IAmqpSession> accept = server.AcceptSessionAsync(cancellationToken).AsTask();

        peerSession = new NetLite.Session(peer);

        return accept;
    }

    private static async Task<IAmqpReceiver> AcceptReceiverAsync(IAmqpConnection connection, CancellationToken cancellationToken)
    {
        IAmqpSession session = await connection.AcceptSessionAsync(cancellationToken);

        return await AcceptReceiverAsync(session, cancellationToken);
    }

    private static async Task<IAmqpReceiver> AcceptReceiverAsync(IAmqpSession session, CancellationToken cancellationToken)
    {
        IAmqpLinkRequest request = await session.AcceptLinkAsync(cancellationToken);

        return await request.AcceptReceiverAsync(cancellationToken: cancellationToken);
    }

    private static async Task<IAmqpSender> AcceptSenderAsync(IAmqpConnection connection, AmqpSenderOptions? options, CancellationToken cancellationToken)
    {
        IAmqpSession session = await connection.AcceptSessionAsync(cancellationToken);

        return await AcceptSenderAsync(session, options, cancellationToken);
    }

    private static async Task<IAmqpSender> AcceptSenderAsync(IAmqpSession session, AmqpSenderOptions? options, CancellationToken cancellationToken)
    {
        IAmqpLinkRequest request = await session.AcceptLinkAsync(cancellationToken);

        return await request.AcceptSenderAsync(options, cancellationToken);
    }

    // AMQPNetLite begins a session of its own for the control link, so the coordinator is the first link of the
    // next session the peer begins, not a link of the session the transactional transfers use.
    private static async Task<AmqpTransactionCoordinator> AcceptCoordinatorAsync(
        IAmqpConnection connection,
        IAmqpTransactionHandler handler,
        CancellationToken cancellationToken)
    {
        IAmqpSession session = await connection.AcceptSessionAsync(cancellationToken);
        IAmqpLinkRequest request = await session.AcceptLinkAsync(cancellationToken);

        request.Target.ShouldBeOfType<AmqpCoordinator>();

        return await AmqpTransactionCoordinator.AcceptAsync(request, handler, cancellationToken: cancellationToken);
    }

    private static async Task<Dictionary<string, List<string>>> CollectAsync(
        IAmqpConnection server,
        int sessions,
        int linksPerSession,
        int messagesPerLink,
        CancellationToken cancellationToken)
    {
        List<Task<(string Name, List<string> Bodies)>> links = [];

        for (int session = 0; session < sessions; session++)
        {
            IAmqpSession accepted = await server.AcceptSessionAsync(cancellationToken);

            for (int link = 0; link < linksPerSession; link++)
            {
                IAmqpLinkRequest request = await accepted.AcceptLinkAsync(cancellationToken);
                IAmqpReceiver receiver = await request.AcceptReceiverAsync(cancellationToken: cancellationToken);

                links.Add(DrainAsync(receiver, messagesPerLink, cancellationToken));
            }
        }

        Dictionary<string, List<string>> received = [];

        foreach ((string name, List<string> bodies) in await Task.WhenAll(links))
        {
            received[name] = bodies;
        }

        return received;
    }

    private static async Task<(string Name, List<string> Bodies)> DrainAsync(IAmqpReceiver receiver, int count, CancellationToken cancellationToken)
    {
        List<string> bodies = [];

        for (int index = 0; index < count; index++)
        {
            IAmqpDelivery delivery = await receiver.ReceiveAsync(cancellationToken);

            bodies.Add((string)delivery.GetMessage().Body.ShouldBeOfType<AmqpValueBody>().Value!);
            await delivery.AcceptAsync(cancellationToken);
        }

        return (receiver.Name, bodies);
    }

    private static Task<NetLiteFraming.Outcome> SendWithOutcomeAsync(NetLite.SenderLink sender, NetLite.Message message)
    {
        TaskCompletionSource<NetLiteFraming.Outcome> outcome = new(TaskCreationOptions.RunContinuationsAsynchronously);

        sender.Send(message, (_, _, settled, _) => outcome.TrySetResult(settled), null);

        return outcome.Task;
    }

    /// <summary>
    /// Receives the delivery a peer's send produces, and fails with the peer's own exception when the send fails
    /// first: a value AMQPNetLite cannot encode then reads as the peer's refusal, not as a broker that never received
    /// the message.
    /// </summary>
    private static async Task<IAmqpDelivery> ReceiveFromPeerAsync(IAmqpReceiver receiver, Task peerSend, CancellationToken cancellationToken)
    {
        Task<IAmqpDelivery> receive = receiver.ReceiveAsync(cancellationToken).AsTask();

        if (await Task.WhenAny(receive, peerSend) == peerSend && peerSend.IsFaulted)
        {
            await peerSend;
        }

        return await receive;
    }

    private static Task WaitForAsync(CancellationToken signal, CancellationToken cancellationToken)
    {
        TaskCompletionSource signaled = new(TaskCreationOptions.RunContinuationsAsynchronously);

        signal.Register(() => signaled.TrySetResult());

        return signaled.Task.WaitAsync(cancellationToken);
    }
}
