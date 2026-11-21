using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Amqp.Connections.AotSample;

/// <summary>
/// The guard's checks, in the order they run. Each one drives a feature end to end over the loopback connection
/// and throws when what arrived is not what was sent.
/// </summary>
internal static class AmqpAotChecks
{
    /// <summary>
    /// Gets every check. The close check runs last because it closes the loopback connection.
    /// </summary>
    public static IReadOnlyList<AmqpAotCheck> All { get; } =
    [
        new("establishment", EstablishmentAsync),
        new("type-system", TypeSystemAsync),
        new("message-sections", MessageSectionsAsync),
        new("outcomes", OutcomesAsync),
        new("pre-settled", PreSettledAsync),
        new("multi-frame", MultiFrameAsync),
        new("transactions", TransactionsAsync),
        new("codec", CodecAsync),
        new("link-refusal", LinkRefusalAsync),
        new("login-refusal", LoginRefusalAsync),
        new("close", CloseAsync)
    ];

    /// <summary>
    /// The protocol headers, SASL PLAIN and both <c>open</c> frames went through, and each end knows the other.
    /// </summary>
    private static Task EstablishmentAsync(AmqpAotLoopback loopback, CancellationToken cancellationToken)
    {
        IAmqpSaslConnectionInfo? sasl = loopback.Server.AmqpSasl;

        Expect(loopback.Client.RemoteContainerId == AmqpAotLoopback.BrokerContainerId, "the client to see the broker's container id");
        Expect(loopback.Server.RemoteContainerId == AmqpAotLoopback.ClientContainerId, "the broker to see the client's container id");
        Expect(loopback.Server.RemoteHostName == AmqpAotLoopback.HostName, "the broker to see the host name the client opened for");
        Expect(
            loopback.Client.MaxFrameSize == AmqpAotLoopback.MaxFrameSize && loopback.Server.MaxFrameSize == AmqpAotLoopback.MaxFrameSize,
            "both ends to agree on the frame size");
        Expect(
            sasl is not null && sasl.Mechanism == new AmqpSymbol("PLAIN") && sasl.Identity == AmqpAotLoopback.UserName,
            "the broker to have authenticated the client with PLAIN");

        return Task.CompletedTask;
    }

    /// <summary>
    /// Every Part 1 type crosses the wire and decodes to the CLR type it was sent as.
    /// </summary>
    private static async Task TypeSystemAsync(AmqpAotLoopback loopback, CancellationToken cancellationToken)
    {
        Dictionary<object, object?> values = new()
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
            ["decimal32"] = new AmqpDecimal32(0x2250_0001u),
            ["decimal64"] = new AmqpDecimal64(0x2238_0000_0000_0001ul),
            ["decimal128"] = new AmqpDecimal128(new UInt128(0x2208_0000_0000_0000ul, 1ul)),
            ["char"] = new Rune(0x1F600),
            ["timestamp"] = DateTimeOffset.FromUnixTimeMilliseconds(1_763_600_000_123),
            ["uuid"] = new Guid("6f9619ff-8b86-d011-b42d-00c04fc964ff"),
            ["binary"] = new byte[] { 0x00, 0x01, 0xFE, 0xFF },
            ["string"] = "Grüße, 世界",
            ["symbol"] = new AmqpSymbol("aot:symbol"),
            ["list"] = new List<object?> { 1, "two", null, new List<object?>() },
            ["map"] = new Dictionary<object, object?> { [new AmqpSymbol("nested")] = 7L, [42] = "answer" },
            ["int-array"] = new[] { 1, 2, 3 },
            ["string-array"] = new[] { "x", "y" },
            ["symbol-array"] = new[] { new AmqpSymbol("a"), new AmqpSymbol("b") },
            ["uuid-array"] = new[] { Guid.Empty },
            ["empty-array"] = Array.Empty<long>(),
            ["ubyte-array"] = new AmqpArray(AmqpPrimitiveType.UByte, [(byte)1, (byte)255]),
            ["described-array"] = new AmqpArray(AmqpPrimitiveType.String, ["a", "b"], new AmqpSymbol("aot:array")),
            ["list-array"] = new AmqpArray(AmqpPrimitiveType.List, [new List<object?> { 1, "two" }, new List<object?>()]),
            ["map-array"] = new AmqpArray(AmqpPrimitiveType.Map, [new Dictionary<object, object?> { ["k"] = 1 }]),
            ["array-array"] = new AmqpArray(AmqpPrimitiveType.Array, [new[] { 1, 2 }, new AmqpArray(AmqpPrimitiveType.UByte, [(byte)3])]),
            ["described"] = new AmqpDescribedValue(new AmqpSymbol("aot:described"), "payload"),
            ["described-ulong"] = new AmqpDescribedValue(0x0000_4141_0000_0001ul, 9)
        };

        (IAmqpSession client, IAmqpSession server) = await loopback.BeginSessionAsync(cancellationToken);
        (IAmqpSender sender, IAmqpReceiver receiver) = await AmqpAotLoopback.AttachAsync(
            client, server, "types", AmqpSenderSettleMode.Unsettled, cancellationToken);

        AmqpMessage received = await TransferAsync(sender, receiver, new AmqpMessage { Body = new AmqpValueBody(values) }, cancellationToken);
        Dictionary<object, object?> arrived = received.Body is AmqpValueBody { Value: Dictionary<object, object?> map }
            ? map
            : throw Failure("the value to arrive as a map");

        Expect(arrived.Count == values.Count, "every entry of the map to arrive");

        foreach (KeyValuePair<object, object?> entry in values)
        {
            Expect(
                arrived.TryGetValue(entry.Key, out object? value) && AmqpAotValues.AreEqual(entry.Value, value),
                $"the {entry.Key} value to arrive as it was sent");
        }

        await client.EndAsync(cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Every Part 3 section arrives as it was sent, and a sequence body keeps its sequences.
    /// </summary>
    private static async Task MessageSectionsAsync(AmqpAotLoopback loopback, CancellationToken cancellationToken)
    {
        DateTimeOffset created = DateTimeOffset.FromUnixTimeMilliseconds(1_763_600_000_000);
        Guid correlationId = new("0f8fad5b-d9cb-469f-a165-70867728950e");
        byte[] userId = Encoding.UTF8.GetBytes(AmqpAotLoopback.UserName);
        AmqpMessage message = new()
        {
            Header = new AmqpHeader { Durable = true, Priority = 7, TimeToLive = 60_000, FirstAcquirer = true, DeliveryCount = 3 },
            DeliveryAnnotations = new Dictionary<AmqpSymbol, object?> { [new AmqpSymbol("x-opt-trace")] = "aot" },
            MessageAnnotations = new Dictionary<AmqpSymbol, object?> { [new AmqpSymbol("x-opt-partition")] = 3 },
            Properties = new AmqpProperties
            {
                MessageId = 42UL,
                UserId = new ReadOnlyMemory<byte>(userId),
                To = "orders",
                Subject = "created",
                ReplyTo = "replies",
                CorrelationId = correlationId,
                ContentType = new AmqpSymbol("application/octet-stream"),
                ContentEncoding = new AmqpSymbol("identity"),
                AbsoluteExpiryTime = created.AddMinutes(1),
                CreationTime = created,
                GroupId = "group",
                GroupSequence = 9,
                ReplyToGroupId = "reply-group"
            },
            ApplicationProperties = new Dictionary<string, object?> { ["region"] = "us-east", ["attempt"] = 2 },
            Body = new AmqpDataBody(new ReadOnlyMemory<byte>[] { new byte[] { 1, 2 }, new byte[] { 3 } }),
            Footer = new Dictionary<AmqpSymbol, object?> { [new AmqpSymbol("x-opt-checksum")] = 0xCAFEu }
        };

        (IAmqpSession client, IAmqpSession server) = await loopback.BeginSessionAsync(cancellationToken);
        (IAmqpSender sender, IAmqpReceiver receiver) = await AmqpAotLoopback.AttachAsync(
            client, server, "sections", AmqpSenderSettleMode.Unsettled, cancellationToken);

        AmqpMessage received = await TransferAsync(sender, receiver, message, cancellationToken);
        AmqpProperties properties = received.Properties ?? throw Failure("the properties to arrive");

        Expect(
            received.Header is { Durable: true, Priority: 7, TimeToLive: 60_000u, FirstAcquirer: true, DeliveryCount: 3u },
            "the header to arrive as it was sent");
        Expect(AmqpAotValues.AreEqual(message.DeliveryAnnotations, received.DeliveryAnnotations), "the delivery annotations to arrive");
        Expect(AmqpAotValues.AreEqual(message.MessageAnnotations, received.MessageAnnotations), "the message annotations to arrive");
        Expect(
            properties.MessageId is 42UL
            && properties.UserId is { } arrivedUserId && arrivedUserId.Span.SequenceEqual(userId)
            && properties.To == "orders"
            && properties.Subject == "created"
            && properties.ReplyTo == "replies"
            && properties.CorrelationId is Guid arrivedCorrelationId && arrivedCorrelationId == correlationId
            && properties.ContentType == new AmqpSymbol("application/octet-stream")
            && properties.ContentEncoding == new AmqpSymbol("identity")
            && properties.AbsoluteExpiryTime == created.AddMinutes(1)
            && properties.CreationTime == created
            && properties.GroupId == "group"
            && properties.GroupSequence == 9
            && properties.ReplyToGroupId == "reply-group",
            "the properties to arrive as they were sent");
        Expect(AmqpAotValues.AreEqual(message.ApplicationProperties, received.ApplicationProperties), "the application properties to arrive");
        Expect(
            received.Body is AmqpDataBody data
            && data.Sections.Count == 2
            && data.Sections[0].Span.SequenceEqual(new byte[] { 1, 2 })
            && data.Sections[1].Span.SequenceEqual(new byte[] { 3 }),
            "both data sections to arrive in order");
        Expect(AmqpAotValues.AreEqual(message.Footer, received.Footer), "the footer to arrive");

        IReadOnlyList<object?>[] sequences = [new List<object?> { 1, "a" }, new List<object?> { true }];
        AmqpMessage sequenced = await TransferAsync(
            sender, receiver, new AmqpMessage { Body = new AmqpSequenceBody(sequences) }, cancellationToken);
        AmqpSequenceBody body = sequenced.Body as AmqpSequenceBody ?? throw Failure("a sequence body to arrive as one");

        Expect(body.Sequences.Count == sequences.Length, "every sequence section to arrive");

        for (int index = 0; index < sequences.Length; index++)
        {
            Expect(SameItems(sequences[index], body.Sequences[index]), $"sequence {index} to arrive as it was sent");
        }

        await client.EndAsync(cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Each of the four outcomes a receiver settles with reaches the sender, with what it carries.
    /// </summary>
    private static async Task OutcomesAsync(AmqpAotLoopback loopback, CancellationToken cancellationToken)
    {
        AmqpSymbol retry = new("x-opt-retry");
        Dictionary<AmqpSymbol, object?> annotations = new() { [retry] = 1 };

        (IAmqpSession client, IAmqpSession server) = await loopback.BeginSessionAsync(cancellationToken);
        (IAmqpSender sender, IAmqpReceiver receiver) = await AmqpAotLoopback.AttachAsync(
            client, server, "outcomes", AmqpSenderSettleMode.Unsettled, cancellationToken);

        AmqpDeliveryState? accepted = await SettleAsync(
            sender, receiver, delivery => delivery.AcceptAsync(cancellationToken), cancellationToken);
        AmqpDeliveryState? rejected = await SettleAsync(
            sender,
            receiver,
            delivery => delivery.RejectAsync(new AmqpError(AmqpErrorCondition.NotAllowed, "refused by the guard"), cancellationToken),
            cancellationToken);
        AmqpDeliveryState? released = await SettleAsync(
            sender, receiver, delivery => delivery.ReleaseAsync(cancellationToken), cancellationToken);
        AmqpDeliveryState? modified = await SettleAsync(
            sender,
            receiver,
            delivery => delivery.ModifyAsync(deliveryFailed: true, undeliverableHere: true, messageAnnotations: annotations, cancellationToken: cancellationToken),
            cancellationToken);

        Expect(accepted is AmqpAccepted, "the sender to learn the delivery was accepted");
        Expect(
            rejected is AmqpRejected { Error: { } error }
            && error.Condition == AmqpErrorCondition.NotAllowed
            && error.Description == "refused by the guard",
            "the sender to learn the delivery was rejected, and why");
        Expect(released is AmqpReleased, "the sender to learn the delivery was released");
        Expect(
            modified is AmqpModified { DeliveryFailed: true, UndeliverableHere: true, MessageAnnotations: { } arrived }
            && arrived.TryGetValue(retry, out object? count)
            && AmqpAotValues.AreEqual(1, count),
            "the sender to learn the delivery was modified, with its annotations");

        await client.EndAsync(cancellationToken: cancellationToken);
    }

    /// <summary>
    /// A link that sends settled delivers at most once: the send completes without an outcome, and the receiver
    /// sees the delivery settled.
    /// </summary>
    private static async Task PreSettledAsync(AmqpAotLoopback loopback, CancellationToken cancellationToken)
    {
        (IAmqpSession client, IAmqpSession server) = await loopback.BeginSessionAsync(cancellationToken);
        (IAmqpSender sender, IAmqpReceiver receiver) = await AmqpAotLoopback.AttachAsync(
            client, server, "pre-settled", AmqpSenderSettleMode.Settled, cancellationToken);

        AmqpDeliveryState? state = await sender.SendAsync(new AmqpMessage { Body = new AmqpValueBody(17) }, cancellationToken: cancellationToken);
        IAmqpDelivery delivery = await receiver.ReceiveAsync(cancellationToken);

        Expect(state is null, "a settled send to complete without an outcome");
        Expect(delivery.IsSettled, "the receiver to see the delivery settled");
        Expect(delivery.GetMessage().Body is AmqpValueBody { Value: 17 }, "the settled delivery to carry its message");

        await client.EndAsync(cancellationToken: cancellationToken);
    }

    /// <summary>
    /// A message far larger than the frame size is split across transfers and reassembled byte for byte.
    /// </summary>
    private static async Task MultiFrameAsync(AmqpAotLoopback loopback, CancellationToken cancellationToken)
    {
        byte[] payload = new byte[256 * 1024];

        for (int index = 0; index < payload.Length; index++)
        {
            payload[index] = (byte)((index * 31) + 7);
        }

        (IAmqpSession client, IAmqpSession server) = await loopback.BeginSessionAsync(cancellationToken);
        (IAmqpSender sender, IAmqpReceiver receiver) = await AmqpAotLoopback.AttachAsync(
            client, server, "multi-frame", AmqpSenderSettleMode.Unsettled, cancellationToken);

        AmqpMessage received = await TransferAsync(sender, receiver, new AmqpMessage { Body = new AmqpDataBody(payload) }, cancellationToken);

        Expect(
            received.Body is AmqpDataBody data && data.Sections.Count == 1 && data.Sections[0].Span.SequenceEqual(payload),
            $"a {payload.Length}-byte body sent in {AmqpAotLoopback.MaxFrameSize}-byte frames to arrive whole");

        await client.EndAsync(cancellationToken: cancellationToken);
    }

    /// <summary>
    /// A controller declares a transaction with a coordinator, posts a transfer under it, learns the resource's
    /// provisional outcome, and commits.
    /// </summary>
    private static async Task TransactionsAsync(AmqpAotLoopback loopback, CancellationToken cancellationToken)
    {
        AmqpAotTransactionHandler handler = new();

        (IAmqpSession client, IAmqpSession server) = await loopback.BeginSessionAsync(cancellationToken);
        Task<AmqpTransactionCoordinator> accept = AcceptCoordinatorAsync(server, handler, cancellationToken);
        AmqpTransactionController controller = await AmqpTransactionController.AttachAsync(client, cancellationToken: cancellationToken);
        AmqpTransactionCoordinator coordinator = await accept;
        Task run = coordinator.RunAsync(cancellationToken);

        ReadOnlyMemory<byte> transaction = await controller.DeclareAsync(cancellationToken);
        (IAmqpSender sender, IAmqpReceiver receiver) = await AmqpAotLoopback.AttachAsync(
            client, server, "transactions", AmqpSenderSettleMode.Unsettled, cancellationToken);
        Task<AmqpDeliveryState?> send = sender.SendAsync(
            new AmqpMessage { Body = new AmqpValueBody("posted in a transaction") },
            new AmqpDeliveryOptions { State = new AmqpTransactionalState(transaction) },
            cancellationToken).AsTask();
        IAmqpDelivery delivery = await receiver.ReceiveAsync(cancellationToken);

        Expect(
            delivery.RemoteState is AmqpTransactionalState posted && posted.TransactionId.Span.SequenceEqual(transaction.Span),
            "the resource to see the transaction the transfer was posted under");

        await delivery.SettleAsync(new AmqpTransactionalState(transaction, AmqpAccepted.Instance), cancellationToken);
        AmqpDeliveryState? state = await send;

        Expect(
            state is AmqpTransactionalState { Outcome: AmqpAccepted } provisional && provisional.TransactionId.Span.SequenceEqual(transaction.Span),
            "the sender to learn the resource's provisional outcome");

        await controller.DischargeAsync(transaction, cancellationToken: cancellationToken);

        IReadOnlyList<(byte[] TransactionId, bool Fail)> discharges = handler.Discharges;

        Expect(
            discharges.Count == 1 && discharges[0].TransactionId.AsSpan().SequenceEqual(transaction.Span) && !discharges[0].Fail,
            "the coordinator to commit the transaction");
        Expect(coordinator.GetActiveTransactions().Count == 0, "no transaction to be left active");

        // Closing the control link ends the coordinator's run, which has nothing left to roll back.
        await controller.DisposeAsync();
        await run;
        await client.EndAsync(cancellationToken: cancellationToken);
    }

    /// <summary>
    /// The codec round-trips a message without a connection, an intermediary replaces the annotations around the
    /// bare message without touching it, and a pre-encoded payload crosses the wire unchanged.
    /// </summary>
    private static async Task CodecAsync(AmqpAotLoopback loopback, CancellationToken cancellationToken)
    {
        AmqpSymbol hop = new("x-opt-hop");
        AmqpMessage message = new()
        {
            Header = new AmqpHeader { Durable = true },
            MessageAnnotations = new Dictionary<AmqpSymbol, object?> { [hop] = 1 },
            Properties = new AmqpProperties { MessageId = "codec-1", Subject = "codec" },
            ApplicationProperties = new Dictionary<string, object?> { ["kind"] = "codec" },
            Body = new AmqpValueBody(new List<object?> { 1L, "two" })
        };

        byte[] encoded = AmqpMessageCodec.Encode(message);
        AmqpMessage decoded = AmqpMessageCodec.Decode(encoded);

        Expect(
            decoded.Header is { Durable: true }
            && decoded.Properties is { MessageId: "codec-1", Subject: "codec" }
            && AmqpAotValues.AreEqual(message.ApplicationProperties, decoded.ApplicationProperties)
            && decoded.Body is AmqpValueBody { Value: List<object?> list }
            && AmqpAotValues.AreEqual(new List<object?> { 1L, "two" }, list),
            "the codec to round-trip a message");

        ReadOnlyMemory<byte> bare = AmqpMessageCodec.GetBareMessage(encoded);
        byte[] forwarded = AmqpMessageCodec.Reannotate(
            encoded,
            new AmqpHeader { DeliveryCount = 1 },
            deliveryAnnotations: null,
            new Dictionary<AmqpSymbol, object?> { [hop] = 2 },
            footer: null);
        AmqpMessage reannotated = AmqpMessageCodec.Decode(forwarded);

        Expect(AmqpMessageCodec.GetBareMessage(forwarded).Span.SequenceEqual(bare.Span), "re-annotating to keep the bare message byte for byte");
        Expect(
            reannotated.Header is { Durable: false, DeliveryCount: 1u }
            && reannotated.MessageAnnotations is { } annotations
            && annotations.TryGetValue(hop, out object? hops)
            && AmqpAotValues.AreEqual(2, hops),
            "the replaced header and annotations to arrive around the bare message");

        (IAmqpSession client, IAmqpSession server) = await loopback.BeginSessionAsync(cancellationToken);
        (IAmqpSender sender, IAmqpReceiver receiver) = await AmqpAotLoopback.AttachAsync(
            client, server, "codec", AmqpSenderSettleMode.Unsettled, cancellationToken);
        Task<AmqpDeliveryState?> send = sender.SendAsync(new ReadOnlyMemory<byte>(forwarded), cancellationToken: cancellationToken).AsTask();
        IAmqpDelivery delivery = await receiver.ReceiveAsync(cancellationToken);

        Expect(delivery.Payload.Span.SequenceEqual(forwarded), "a pre-encoded payload to cross the wire unchanged");

        await delivery.AcceptAsync(cancellationToken);

        Expect(await send is AmqpAccepted, "the sender to learn the pre-encoded delivery was accepted");

        await client.EndAsync(cancellationToken: cancellationToken);
    }

    /// <summary>
    /// A link the application refuses fails the peer's attach with the error the application gave.
    /// </summary>
    private static async Task LinkRefusalAsync(AmqpAotLoopback loopback, CancellationToken cancellationToken)
    {
        (IAmqpSession client, IAmqpSession server) = await loopback.BeginSessionAsync(cancellationToken);
        Task<IAmqpSender> attach = client.AttachSenderAsync(
            new AmqpSenderOptions { Name = "missing-sender", Target = new AmqpTarget { Address = "missing" } },
            cancellationToken).AsTask();
        IAmqpLinkRequest request = await server.AcceptLinkAsync(cancellationToken);

        await request.RejectAsync(new AmqpError(AmqpErrorCondition.NotFound, "no such node"), cancellationToken);

        AmqpException refusal = await ExpectThrowsAsync<AmqpException>(attach, "the refused attach to fail");

        Expect(
            refusal.ErrorCode == AmqpErrorCode.LinkDetached && refusal.Error is { } error && error.Condition == AmqpErrorCondition.NotFound,
            "the refused attach to carry the application's error");

        await client.EndAsync(cancellationToken: cancellationToken);
    }

    /// <summary>
    /// A wrong password fails the client's connect, and the listener, which contained the failure, accepts the next
    /// connection.
    /// </summary>
    private static async Task LoginRefusalAsync(AmqpAotLoopback loopback, CancellationToken cancellationToken)
    {
        AmqpException refusal = await ExpectThrowsAsync<AmqpException>(
            AmqpAotLoopback.ConnectAsync(loopback.Listener, "wrong-password", cancellationToken).AsTask(),
            "a wrong password to fail the connect");

        Expect(
            refusal.ErrorCode == AmqpErrorCode.AuthenticationFailed,
            $"the failed connect to report the authentication failure, not {refusal.ErrorCode}: {refusal.Message} ({refusal.InnerException?.GetType().FullName}: {refusal.InnerException?.Message})");

        Task<IAmqpConnection> accept = loopback.Listener.AcceptAsync(cancellationToken).AsTask();

        await using IAmqpConnection client = await AmqpAotLoopback.ConnectAsync(loopback.Listener, AmqpAotLoopback.Password, cancellationToken);
        await using IAmqpConnection server = await accept;

        Expect(server.RemoteContainerId == AmqpAotLoopback.ClientContainerId, "the listener to accept the next connection");
    }

    /// <summary>
    /// The broker closes the connection with an error: the client learns it, and every session on both ends ends.
    /// </summary>
    private static async Task CloseAsync(AmqpAotLoopback loopback, CancellationToken cancellationToken)
    {
        (IAmqpSession client, IAmqpSession server) = await loopback.BeginSessionAsync(cancellationToken);

        await loopback.Server.CloseAsync(new AmqpError(AmqpErrorCondition.ConnectionForced, "maintenance"), cancellationToken);

        AmqpException closed = await ExpectThrowsAsync<AmqpException>(
            loopback.Client.BeginSessionAsync(cancellationToken: cancellationToken).AsTask(),
            "a session on the closed connection to fail");

        Expect(
            closed.ErrorCode == AmqpErrorCode.ConnectionClosed && closed.Error is { } error && error.Condition == AmqpErrorCondition.ConnectionForced,
            "the client to learn why the broker closed");
        Expect(
            loopback.Client.ConnectionClosed.IsCancellationRequested && client.SessionEnded.IsCancellationRequested && server.SessionEnded.IsCancellationRequested,
            "the connection and every session to end on both sides");
    }

    private static async Task<AmqpMessage> TransferAsync(
        IAmqpSender sender,
        IAmqpReceiver receiver,
        AmqpMessage message,
        CancellationToken cancellationToken)
    {
        Task<AmqpDeliveryState?> send = sender.SendAsync(message, cancellationToken: cancellationToken).AsTask();
        IAmqpDelivery delivery = await receiver.ReceiveAsync(cancellationToken);
        AmqpMessage received = delivery.GetMessage();

        await delivery.AcceptAsync(cancellationToken);

        Expect(await send is AmqpAccepted, "the sender to learn the delivery was accepted");

        return received;
    }

    private static async Task<AmqpDeliveryState?> SettleAsync(
        IAmqpSender sender,
        IAmqpReceiver receiver,
        Func<IAmqpDelivery, ValueTask> settle,
        CancellationToken cancellationToken)
    {
        Task<AmqpDeliveryState?> send = sender.SendAsync(
            new AmqpMessage { Body = new AmqpValueBody("settle me") },
            cancellationToken: cancellationToken).AsTask();
        IAmqpDelivery delivery = await receiver.ReceiveAsync(cancellationToken);

        await settle(delivery);

        return await send;
    }

    private static async Task<AmqpTransactionCoordinator> AcceptCoordinatorAsync(
        IAmqpSession session,
        IAmqpTransactionHandler handler,
        CancellationToken cancellationToken)
    {
        IAmqpLinkRequest request = await session.AcceptLinkAsync(cancellationToken);

        return await AmqpTransactionCoordinator.AcceptAsync(request, handler, cancellationToken: cancellationToken);
    }

    private static bool SameItems(IReadOnlyList<object?> expected, IReadOnlyList<object?> actual)
    {
        if (expected.Count != actual.Count)
        {
            return false;
        }

        for (int index = 0; index < expected.Count; index++)
        {
            if (!AmqpAotValues.AreEqual(expected[index], actual[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static async Task<TException> ExpectThrowsAsync<TException>(Task task, string expectation)
        where TException : Exception
    {
        try
        {
            await task;
        }
        catch (TException exception)
        {
            return exception;
        }

        throw Failure(expectation);
    }

    private static void Expect(bool condition, string expectation)
    {
        if (!condition)
        {
            throw Failure(expectation);
        }
    }

    private static InvalidOperationException Failure(string expectation)
    {
        return new InvalidOperationException("Expected " + expectation + ".");
    }
}
