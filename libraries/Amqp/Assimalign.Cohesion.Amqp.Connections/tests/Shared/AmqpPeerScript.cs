using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Assimalign.Cohesion.Amqp;
using Assimalign.Cohesion.Amqp.Connections.Internal;

namespace Assimalign.Cohesion.Amqp.Connections.Tests;

/// <summary>
/// The steps a scripted peer takes against the engine's links: beginning a session, attaching a link from either
/// end on handle 0, building the frames it sends, and reading the frames the engine answers with.
/// </summary>
internal static class AmqpPeerScript
{
    /// <summary>
    /// The peer begins a session, and the engine accepts it.
    /// </summary>
    public static async Task<IAmqpSession> BeginAsync(AmqpScriptedHost host, CancellationToken cancellationToken)
    {
        await host.Peer.BeginAsync(null, cancellationToken);
        return await host.Connection.AcceptSessionAsync(cancellationToken);
    }

    /// <summary>
    /// Creates the first, or only, frame of a delivery on handle 0, tagged with its delivery-id unless a tag is given.
    /// </summary>
    public static AmqpTransferPerformative CreateTransfer(
        uint deliveryId,
        bool settled = false,
        AmqpDeliveryState? state = null,
        AmqpDeliveryTag? deliveryTag = null)
    {
        return new AmqpTransferPerformative
        {
            Handle = 0,
            DeliveryId = deliveryId,
            DeliveryTag = deliveryTag ?? AmqpDeliveryTag.Create(deliveryId),
            MessageFormat = 0,
            Settled = settled,
            State = state
        };
    }

    /// <summary>
    /// Marks a delivery's first frame as one more frames follow.
    /// </summary>
    public static AmqpTransferPerformative First(AmqpTransferPerformative transfer)
    {
        transfer.More = true;
        return transfer;
    }

    /// <summary>
    /// Creates a later frame of the delivery in progress on handle 0, which names only the link.
    /// </summary>
    public static AmqpTransferPerformative Continuation(bool more = false)
    {
        return new AmqpTransferPerformative { Handle = 0, More = more };
    }

    /// <summary>
    /// Creates the <c>attach</c> of a sending link the peer attaches on handle 0.
    /// </summary>
    public static AmqpAttachPerformative CreateSenderAttach(IAmqpTarget target)
    {
        return new AmqpAttachPerformative
        {
            Name = "link",
            Handle = 0,
            Role = AmqpRole.Sender,
            Source = new AmqpSource { Address = "peer" },
            Target = target,
            InitialDeliveryCount = 0
        };
    }

    /// <summary>
    /// Creates the <c>attach</c> of a receiving link the peer attaches on handle 0.
    /// </summary>
    public static AmqpAttachPerformative CreateReceiverAttach(IAmqpTarget target)
    {
        return new AmqpAttachPerformative
        {
            Name = "link",
            Handle = 0,
            Role = AmqpRole.Receiver,
            Source = new AmqpSource { Address = "queue" },
            Target = target
        };
    }

    /// <summary>
    /// Creates a <c>flow</c> that grants the engine's sender on handle 0 link credit.
    /// </summary>
    public static AmqpFlowPerformative CreateLinkFlow(AmqpBeginPerformative engineBegin, uint credit, byte[]? transactionId = null, uint incomingWindow = 100)
    {
        return new AmqpFlowPerformative
        {
            NextIncomingId = engineBegin.NextOutgoingId,
            IncomingWindow = incomingWindow,
            NextOutgoingId = 1,
            OutgoingWindow = 100,
            Handle = 0,
            DeliveryCount = 0,
            LinkCredit = credit,
            Properties = transactionId is null ? null : new Dictionary<AmqpSymbol, object?> { [new AmqpSymbol("txn-id")] = transactionId }
        };
    }

    /// <summary>
    /// The peer attaches a sending link; the engine accepts it as a receiver.
    /// </summary>
    public static Task<IAmqpReceiver> AttachPeerSenderAsync(
        AmqpTestPeer peer,
        IAmqpSession session,
        IAmqpTarget target,
        AmqpReceiverOptions? options,
        CancellationToken cancellationToken)
    {
        return AttachPeerSenderAsync(peer, session, CreateSenderAttach(target), options, cancellationToken);
    }

    /// <summary>
    /// The peer attaches a sending link with the specified <c>attach</c>; the engine accepts it as a receiver.
    /// </summary>
    public static async Task<IAmqpReceiver> AttachPeerSenderAsync(
        AmqpTestPeer peer,
        IAmqpSession session,
        AmqpAttachPerformative attach,
        AmqpReceiverOptions? options,
        CancellationToken cancellationToken)
    {
        await peer.WriteFrameAsync(0, attach, cancellationToken);

        IAmqpLinkRequest request = await session.AcceptLinkAsync(cancellationToken);
        IAmqpReceiver receiver = await request.AcceptReceiverAsync(options, cancellationToken);

        await ReadUntilAsync<AmqpAttachPerformative>(peer, cancellationToken);
        return receiver;
    }

    /// <summary>
    /// The peer attaches a receiving link; the engine accepts it as a sender.
    /// </summary>
    public static Task<IAmqpSender> AttachPeerReceiverAsync(
        AmqpTestPeer peer,
        IAmqpSession session,
        AmqpSenderOptions? options,
        CancellationToken cancellationToken)
    {
        return AttachPeerReceiverAsync(peer, session, CreateReceiverAttach(new AmqpTarget { Address = "peer" }), options, cancellationToken);
    }

    /// <summary>
    /// The peer attaches a receiving link with the specified <c>attach</c>; the engine accepts it as a sender.
    /// </summary>
    public static async Task<IAmqpSender> AttachPeerReceiverAsync(
        AmqpTestPeer peer,
        IAmqpSession session,
        AmqpAttachPerformative attach,
        AmqpSenderOptions? options,
        CancellationToken cancellationToken)
    {
        await peer.WriteFrameAsync(0, attach, cancellationToken);

        IAmqpLinkRequest request = await session.AcceptLinkAsync(cancellationToken);
        IAmqpSender sender = await request.AcceptSenderAsync(options, cancellationToken);

        await ReadUntilAsync<AmqpAttachPerformative>(peer, cancellationToken);
        return sender;
    }

    /// <summary>
    /// Writes a <c>flow</c> that asks for an echo, and waits for the echo, which the engine sends once it applied
    /// the flow.
    /// </summary>
    public static async Task WriteFlowAndWaitAsync(AmqpTestPeer peer, AmqpFlowPerformative flow, CancellationToken cancellationToken)
    {
        flow.Echo = true;
        await peer.WriteFrameAsync(0, flow, cancellationToken);
        await ReadUntilAsync<AmqpFlowPerformative>(peer, cancellationToken);
    }

    /// <summary>
    /// Waits until the engine has applied every frame the peer sent before: it answers a session <c>flow</c> that
    /// asks for an echo only once it reaches it.
    /// </summary>
    /// <remarks>
    /// The flow names no next-incoming-id, so the engine counts the transfers it sent as unseen: use it where the
    /// engine sends no transfers, or where its outgoing window does not matter.
    /// </remarks>
    public static async Task SyncAsync(AmqpTestPeer peer, CancellationToken cancellationToken)
    {
        await peer.WriteFrameAsync(0, new AmqpFlowPerformative { IncomingWindow = 100, NextOutgoingId = 1, OutgoingWindow = 100, Echo = true }, cancellationToken);

        // Link flows granting credit may come first.
        while ((await ReadUntilAsync<AmqpFlowPerformative>(peer, cancellationToken)).Handle is not null)
        {
        }
    }

    /// <summary>
    /// Reads the frames of the next delivery the engine sends, through its last frame.
    /// </summary>
    public static async Task<List<AmqpTestFrame>> ReadDeliveryFramesAsync(AmqpTestPeer peer, CancellationToken cancellationToken)
    {
        List<AmqpTestFrame> frames = [];

        while (true)
        {
            AmqpTestFrame frame = await ReadFrameUntilAsync<AmqpTransferPerformative>(peer, cancellationToken);
            AmqpTransferPerformative transfer = (AmqpTransferPerformative)frame.Performative!;

            frames.Add(frame);

            if (!transfer.More || transfer.Aborted)
            {
                return frames;
            }
        }
    }

    /// <summary>
    /// Reads the <c>transfer</c> frames of the next delivery the engine sends, through its last frame.
    /// </summary>
    public static async Task<List<AmqpTransferPerformative>> ReadDeliveryAsync(AmqpTestPeer peer, CancellationToken cancellationToken)
    {
        List<AmqpTransferPerformative> transfers = [];

        foreach (AmqpTestFrame frame in await ReadDeliveryFramesAsync(peer, cancellationToken))
        {
            transfers.Add((AmqpTransferPerformative)frame.Performative!);
        }

        return transfers;
    }

    /// <summary>
    /// Waits until a receiver holds part of a delivery that carried a state.
    /// </summary>
    public static async Task<AmqpDeliveryState?> WaitForPartialStateAsync(IAmqpReceiver receiver, CancellationToken cancellationToken)
    {
        while (receiver.PartialDeliveryState is null)
        {
            await Task.Delay(10, cancellationToken);
        }

        return receiver.PartialDeliveryState;
    }

    /// <summary>
    /// Reads frames until one carries a <typeparamref name="T"/>, which it returns, and fails if the engine closes
    /// the connection first.
    /// </summary>
    public static async Task<T> ReadUntilAsync<T>(AmqpTestPeer peer, CancellationToken cancellationToken)
        where T : AmqpPerformative
    {
        AmqpTestFrame frame = await ReadFrameUntilAsync<T>(peer, cancellationToken);

        return (T)frame.Performative!;
    }

    /// <summary>
    /// Reads frames until one carries a <typeparamref name="T"/>, and returns that frame with its payload.
    /// </summary>
    public static async Task<AmqpTestFrame> ReadFrameUntilAsync<T>(AmqpTestPeer peer, CancellationToken cancellationToken)
        where T : AmqpPerformative
    {
        List<string> skipped = [];

        while (true)
        {
            AmqpTestFrame frame = await peer.ReadNonEmptyFrameAsync(cancellationToken);

            if (frame.Performative is T)
            {
                return frame;
            }

            frame.Performative.ShouldNotBeOfType<AmqpClosePerformative>($"The engine closed the connection after {string.Join(", ", skipped)}.");
            skipped.Add(frame.Performative!.PerformativeName);
        }
    }
}
