using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Amqp;
using Assimalign.Cohesion.Amqp.Connections.Internal;
using Assimalign.Cohesion.Connections;

using static Assimalign.Cohesion.Amqp.Connections.Tests.AmqpPeerScript;

namespace Assimalign.Cohesion.Amqp.Connections.Tests;

/// <summary>
/// Drives an open connection with a scripted peer through the rules of AMQP 1.0 Part 2 for a connection: the limits
/// both <c>open</c> frames set, the frames that may travel once it is open, closing it from either end or both, and
/// its idle timeout. Each rule in <see cref="Rules"/> names the tests that pin it.
/// </summary>
public class AmqpConnectionComplianceTests
{
    private static readonly TimeSpan _testTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Gets the Part 2 connection rules the engine holds, each with the tests that pin it.
    /// </summary>
    internal static IReadOnlyList<AmqpComplianceRule> Rules { get; } =
    [
        new("C1", "Each end declares in open the largest frame and the highest channel it accepts, and the connection takes the smaller of each: neither end writes a larger frame or uses a higher channel.", "Part 2, sections 2.4.1 and 2.7.1",
            Tests:
            [
                nameof(AmqpConnectionTests.ConnectAsync_OnListener_ShouldOpenBothEndsWithNegotiatedLimits),
                nameof(AmqpTransferComplianceTests.SendAsync_OnDeliveryLargerThanFrame_ShouldSplitIt)
            ],
            Refusals: []),
        new("C2", "A frame larger than the max-frame-size the engine declared, or on a channel above the channel-max it declared, closes the connection with amqp:connection:framing-error; a frame of exactly max-frame-size is accepted.", "Part 2, section 2.7.1",
            Tests:
            [
                nameof(AmqpEngineViolationTests.ProcessFrame_OnFrameOfExactlyMaxFrameSize_ShouldAccept),
                nameof(AmqpFrameTests.TryRead_WithFrameOfExactlyMaxFrameSize_ShouldReadFrame)
            ],
            Refusals:
            [
                nameof(AmqpEngineViolationTests.ProcessFrame_OnFrameAboveMaxFrameSize_ShouldCloseWithFramingError),
                nameof(AmqpEngineViolationTests.ProcessFrame_OnChannelAboveChannelMax_ShouldCloseWithFramingError),
                nameof(AmqpFrameTests.TryRead_WithOversizedFrameHeader_ShouldThrowFramingError)
            ]),
        new("C3", "A performative too large for the peer's max-frame-size is refused with amqp:frame-size-too-small before anything is written, and leaves the connection, its session and the handle it would have taken as they were; a delivery is split across transfers instead.", "Part 2, sections 2.7.1 and 2.8.15",
            Tests: [nameof(AmqpTransferComplianceTests.SendAsync_OnDeliveryLargerThanFrame_ShouldSplitIt)],
            Refusals: [nameof(AttachSenderAsync_OnAttachLargerThanPeerFrame_ShouldRefuseWithFrameSizeTooSmall)]),
        new("C4", "Once the connection is open only AMQP frames travel, and open is not sent again: a second open or a SASL performative closes the connection with amqp:illegal-state, and a SASL frame closes it with amqp:connection:framing-error.", "Part 2, sections 2.3.1 and 2.4.1",
            Tests: [],
            Refusals:
            [
                nameof(AmqpEngineViolationTests.ProcessFrame_OnSecondOpen_ShouldCloseWithIllegalState),
                nameof(ProcessFrame_OnSaslPerformativeInAmqpFrame_ShouldCloseWithIllegalState),
                nameof(AmqpEngineViolationTests.ProcessFrame_OnSaslFrameAfterOpen_ShouldCloseWithFramingError)
            ]),
        new("C5", "A close is answered with a close: the engine answers a peer's close and ends with the peer's error, and a close the engine sends ends when the peer's close arrives.", "Part 2, sections 2.4.3 and 2.7.9",
            Tests:
            [
                nameof(AmqpEngineViolationTests.OnClose_FromPeer_ShouldAnswerAndCloseWithPeerError),
                nameof(AmqpConnectionTests.CloseAsync_WithError_ShouldCloseBothEnds)
            ],
            Refusals: []),
        new("C6", "When both ends close at once, each takes the other's close as the answer to its own: the engine writes no second close and does not wait for one.", "Part 2, section 2.4.4",
            Tests: [nameof(CloseAsync_OnPeerCloseCrossingIt_ShouldTakeItAsTheAnswer)],
            Refusals: []),
        new("C7", "Close is the last frame a peer writes: a carrier that ends before the peer's close leaves the connection aborted, not closed.", "Part 2, section 2.4.3",
            Tests: [nameof(AmqpEngineViolationTests.ProcessFrame_OnCarrierEnded_ShouldAbortConnection)],
            Refusals: []),
        new("C8", "Each end keeps the idle timeout the other declared: the engine declares half its own, writes an empty frame before it would stay silent for half the peer's, and closes a connection whose peer stays silent past its own with amqp:resource-limit-exceeded.", "Part 2, section 2.4.5",
            Tests:
            [
                nameof(AmqpEngineViolationTests.OnIdleTimer_OnPeerIdleTimeout_ShouldSendEmptyFrames),
                nameof(AmqpConnectionTests.IdleTimeout_OnIdleConnection_ShouldKeepConnectionOpen),
                nameof(AmqpIdleTimerTests.GetTimerPeriod_OnKeptRemoteIdleTimeout_ShouldBoundTheLongestGap)
            ],
            Refusals: [nameof(AmqpEngineViolationTests.OnIdleTimer_OnSilentPeer_ShouldCloseWithResourceLimitExceeded)])
    ];

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - AttachSenderAsync: Should refuse an attach larger than the peer's frames with frame-size-too-small before writing it")]
    public async Task AttachSenderAsync_OnAttachLargerThanPeerFrame_ShouldRefuseWithFrameSizeTooSmall()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(null, AmqpTestPeer.CreateOpen(maxFrameSize: 512), timeout.Token);
        AmqpTestPeer peer = host.Peer;
        IAmqpSession session = await BeginAsync(host, timeout.Token);
        AmqpSenderOptions oversized = new() { Name = new string('n', 600), Target = new AmqpTarget { Address = "queue" } };

        // Act
        AmqpException exception = await Should.ThrowAsync<AmqpException>(async () => await session.AttachSenderAsync(oversized, timeout.Token));
        AmqpPerformative next = await ReadNextAfterEchoAsync(peer, timeout.Token);
        Task<IAmqpSender> attach = session.AttachSenderAsync(new AmqpSenderOptions { Name = "fits", Target = new AmqpTarget { Address = "queue" } }, timeout.Token).AsTask();
        AmqpAttachPerformative fitting = await ReadUntilAsync<AmqpAttachPerformative>(peer, timeout.Token);
        await peer.WriteFrameAsync(0, new AmqpAttachPerformative
        {
            Name = fitting.Name,
            Handle = 0,
            Role = AmqpRole.Receiver,
            Source = fitting.Source,
            Target = fitting.Target
        }, timeout.Token);
        IAmqpSender sender = await attach;

        // Assert
        exception.ErrorCode.ShouldBe(AmqpErrorCode.Other);
        exception.Error.ShouldNotBeNull().Condition.ShouldBe(AmqpErrorCondition.FrameSizeTooSmall);
        next.ShouldBeOfType<AmqpFlowPerformative>().Handle.ShouldBeNull();
        fitting.Handle.ShouldBe(0u);
        sender.Name.ShouldBe("fits");
        session.SessionEnded.IsCancellationRequested.ShouldBeFalse();
        host.Connection.State.ShouldBe(ConnectionState.Open);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - ProcessFrame: Should close the connection when an AMQP frame carries a SASL performative")]
    public async Task ProcessFrame_OnSaslPerformativeInAmqpFrame_ShouldCloseWithIllegalState()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(cancellationToken: timeout.Token);
        AmqpTestPeer peer = host.Peer;

        // Act
        await peer.WriteFrameAsync(0, new AmqpSaslOutcomePerformative { Code = AmqpSaslCode.Ok }, timeout.Token);
        AmqpError? error = await peer.ReadCloseAsync(timeout.Token);

        // Assert
        error.ShouldNotBeNull().Condition.ShouldBe(AmqpErrorCondition.IllegalState);
        error.Description.ShouldBe(string.Format(CultureInfo.CurrentCulture, ErrorMessages.PerformativeUnexpected, "sasl-outcome"));
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - CloseAsync: Should take a close the peer sent at the same time as the answer to its own")]
    public async Task CloseAsync_OnPeerCloseCrossingIt_ShouldTakeItAsTheAnswer()
    {
        // Arrange: a close timeout longer than the test's, so only taking the peer's close as the answer ends the close in time.
        using CancellationTokenSource timeout = new(_testTimeout);
        AmqpConnectionListenerOptions options = new() { HandshakeTimeout = TimeSpan.FromSeconds(30) };
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(options, null, timeout.Token);
        AmqpTestPeer peer = host.Peer;
        IAmqpConnection connection = host.Connection;

        // Act: the peer closes for its own reason before it reads the engine's close.
        Task close = connection.CloseAsync(new AmqpError(AmqpErrorCondition.ConnectionForced, "maintenance")).AsTask();
        await peer.WriteFrameAsync(0, new AmqpClosePerformative { Error = new AmqpError(AmqpErrorCondition.ResourceLimitExceeded) }, timeout.Token);
        await close.WaitAsync(timeout.Token);
        IReadOnlyList<AmqpPerformative> written = await peer.ReadToEndAsync(timeout.Token);

        // Assert
        written.Count.ShouldBe(1);
        written.Single().ShouldBeOfType<AmqpClosePerformative>().Error.ShouldNotBeNull().Condition.ShouldBe(AmqpErrorCondition.ConnectionForced);
        connection.State.ShouldBe(ConnectionState.Closed);
    }
}
