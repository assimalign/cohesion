using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Amqp;
using Assimalign.Cohesion.Amqp.Connections.Internal;

namespace Assimalign.Cohesion.Amqp.Connections.Tests;

/// <summary>
/// Covers the rules on SASL frames (AMQP 1.0 Part 5, section 5.3.1): no SASL frame in either direction
/// exceeds MIN-MAX-FRAME-SIZE (512 bytes), a peer's frame is refused from its header before its body
/// arrives, an empty frame is an irrecoverable error, and a frame body holds exactly one value.
/// </summary>
public class AmqpSaslFrameTests
{
    private static readonly TimeSpan _testTimeout = TimeSpan.FromSeconds(10);

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - ConnectAsync: Should refuse a SASL frame above 512 bytes from its header, before its body")]
    public async Task ConnectAsync_OnSaslFrameAbove512Bytes_ShouldRefuseFromHeader()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        (AmqpTestPeer peer, AmqpConnectionFactory factory, Task<IAmqpConnection> connect) = AmqpTestPeer.StartFactory(
            new AmqpConnectionFactoryOptions { SaslMechanism = new AmqpSaslAnonymousClientMechanism() },
            timeout.Token);
        await peer.ReadProtocolHeaderAsync(timeout.Token);
        await peer.WriteProtocolHeaderAsync(AmqpFrame.SaslProtocolId, timeout.Token);

        // Act: only the header of a 513-byte frame is sent, so waiting for its body would time out.
        await peer.WriteAsync(Convert.FromHexString("0000020102010000"), timeout.Token);
        AmqpException exception = await Should.ThrowAsync<AmqpException>(connect);

        // Assert
        exception.ErrorCode.ShouldBe(AmqpErrorCode.ProtocolViolation);
        exception.Message.ShouldBe(string.Format(ErrorMessages.PeerSaslFrameTooLarge, 513, 512));
        peer.Carrier.IsDisposed.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - AcceptAsync: Should hang up on a SASL frame above 512 bytes from its header, before its body")]
    public async Task AcceptAsync_OnSaslFrameAbove512Bytes_ShouldHangUpFromHeader()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        AmqpConnectionListenerOptions options = new() { MaxFrameSize = 65536 };
        options.SaslMechanisms.Add(new AmqpSaslAnonymousServerMechanism());
        (AmqpTestPeer peer, AmqpConnectionListener listener, Task<IAmqpConnection> accept) = AmqpTestPeer.StartListener(options, timeout.Token);
        await using AmqpConnectionListener owned = listener;
        await peer.WriteProtocolHeaderAsync(AmqpFrame.SaslProtocolId, timeout.Token);
        await peer.ReadProtocolHeaderAsync(timeout.Token);
        await peer.ReadPerformativeAsync<AmqpSaslMechanismsPerformative>(timeout.Token);

        // Act: the listener's own maximum frame size is far larger, but no SASL frame may exceed 512 bytes.
        await peer.WriteAsync(Convert.FromHexString("0000040002010000"), timeout.Token);
        IReadOnlyList<AmqpPerformative> rest = await peer.ReadToEndAsync(timeout.Token);

        // Assert
        rest.ShouldBeEmpty();
        accept.IsCompleted.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - ConnectAsync: Should accept a SASL frame of exactly 512 bytes")]
    public async Task ConnectAsync_OnSaslFrameOfExactly512Bytes_ShouldContinue()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        AmqpSaslMechanismsPerformative offered = CreateMechanismsOfFrameSize(512);
        (AmqpTestPeer peer, AmqpConnectionFactory factory, Task<IAmqpConnection> connect) = AmqpTestPeer.StartFactory(
            new AmqpConnectionFactoryOptions { SaslMechanism = new AmqpSaslAnonymousClientMechanism() },
            timeout.Token);
        await using AmqpTestPeer hangUp = peer;
        await peer.ReadProtocolHeaderAsync(timeout.Token);
        await peer.WriteProtocolHeaderAsync(AmqpFrame.SaslProtocolId, timeout.Token);

        // Act
        await peer.WriteFrameAsync(0, offered, timeout.Token, type: AmqpFrame.SaslType);
        AmqpSaslInitPerformative init = await peer.ReadPerformativeAsync<AmqpSaslInitPerformative>(timeout.Token);

        // Assert
        init.Mechanism.ShouldBe((AmqpSymbol)"ANONYMOUS");
    }

    [Theory(DisplayName = "Cohesion Test [Amqp.Connections] - ConnectAsync: Should refuse an empty frame during the SASL exchange")]
    [InlineData(AmqpFrame.SaslType)]
    [InlineData(AmqpFrame.AmqpType)]
    public async Task ConnectAsync_OnEmptySaslFrame_ShouldRefuse(byte type)
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        (AmqpTestPeer peer, AmqpConnectionFactory factory, Task<IAmqpConnection> connect) = AmqpTestPeer.StartFactory(
            new AmqpConnectionFactoryOptions { SaslMechanism = new AmqpSaslAnonymousClientMechanism() },
            timeout.Token);
        await peer.ReadProtocolHeaderAsync(timeout.Token);
        await peer.WriteProtocolHeaderAsync(AmqpFrame.SaslProtocolId, timeout.Token);

        // Act
        await peer.WriteAsync(new byte[] { 0, 0, 0, 8, 2, type, 0, 0 }, timeout.Token);
        AmqpException exception = await Should.ThrowAsync<AmqpException>(connect);

        // Assert
        exception.ErrorCode.ShouldBe(AmqpErrorCode.ProtocolViolation);
        exception.Message.ShouldBe(ErrorMessages.SaslFrameEmpty);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - AcceptAsync: Should hang up on an empty frame during the SASL exchange")]
    public async Task AcceptAsync_OnEmptySaslFrame_ShouldHangUp()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        AmqpConnectionListenerOptions options = new();
        options.SaslMechanisms.Add(new AmqpSaslAnonymousServerMechanism());
        (AmqpTestPeer peer, AmqpConnectionListener listener, Task<IAmqpConnection> accept) = AmqpTestPeer.StartListener(options, timeout.Token);
        await using AmqpConnectionListener owned = listener;
        await peer.WriteProtocolHeaderAsync(AmqpFrame.SaslProtocolId, timeout.Token);
        await peer.ReadProtocolHeaderAsync(timeout.Token);
        await peer.ReadPerformativeAsync<AmqpSaslMechanismsPerformative>(timeout.Token);

        // Act
        await peer.WriteAsync(new byte[] { 0, 0, 0, 8, 2, AmqpFrame.SaslType, 0, 0 }, timeout.Token);
        IReadOnlyList<AmqpPerformative> rest = await peer.ReadToEndAsync(timeout.Token);

        // Assert
        rest.ShouldBeEmpty();
        accept.IsCompleted.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - ConnectAsync: Should refuse a SASL frame whose body holds more than one value")]
    public async Task ConnectAsync_OnSaslFrameWithTrailingBytes_ShouldRefuse()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        (AmqpTestPeer peer, AmqpConnectionFactory factory, Task<IAmqpConnection> connect) = AmqpTestPeer.StartFactory(
            new AmqpConnectionFactoryOptions { SaslMechanism = new AmqpSaslAnonymousClientMechanism() },
            timeout.Token);
        await peer.ReadProtocolHeaderAsync(timeout.Token);
        await peer.WriteProtocolHeaderAsync(AmqpFrame.SaslProtocolId, timeout.Token);

        // Act: a null follows the sasl-mechanisms value inside the same frame body.
        await peer.WriteFrameAsync(0, new AmqpSaslMechanismsPerformative { Mechanisms = ["ANONYMOUS"] }, timeout.Token, payload: new byte[] { 0x40 }, type: AmqpFrame.SaslType);
        AmqpException exception = await Should.ThrowAsync<AmqpException>(connect);

        // Assert
        exception.ErrorCode.ShouldBe(AmqpErrorCode.ProtocolViolation);
        exception.Message.ShouldBe(ErrorMessages.SaslFrameBodyInvalid);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - ConnectAsync: Should refuse to send a sasl-init above 512 bytes and send nothing")]
    public async Task ConnectAsync_WithInitialResponseAbove512Bytes_ShouldThrowWithoutSending()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        (AmqpTestPeer peer, AmqpConnectionFactory factory, Task<IAmqpConnection> connect) = AmqpTestPeer.StartFactory(
            new AmqpConnectionFactoryOptions { SaslMechanism = AmqpSaslPlainClientMechanism.Create("alice", new string('t', 600)) },
            timeout.Token);
        await peer.ReadProtocolHeaderAsync(timeout.Token);
        await peer.WriteProtocolHeaderAsync(AmqpFrame.SaslProtocolId, timeout.Token);

        // Act
        await peer.WriteFrameAsync(0, new AmqpSaslMechanismsPerformative { Mechanisms = ["PLAIN"] }, timeout.Token, type: AmqpFrame.SaslType);
        AmqpException exception = await Should.ThrowAsync<AmqpException>(connect);
        IReadOnlyList<AmqpPerformative> sent = await peer.ReadToEndAsync(timeout.Token);

        // Assert
        exception.ErrorCode.ShouldBe(AmqpErrorCode.Other);
        exception.Message.ShouldStartWith("The local 'sasl-init' encodes to a SASL frame of ");
        exception.Message.ShouldContain("512", Case.Sensitive);
        sent.ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Amqp.Connections] - AcceptAsync: Should answer a mechanism step too large for a SASL frame with a system outcome")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AcceptAsync_OnMechanismStepAbove512Bytes_ShouldSendSystemOutcome(bool challenge)
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        AmqpConnectionListenerOptions options = new();
        options.SaslMechanisms.Add(new TestOversizedSaslServerMechanism(challenge));
        (AmqpTestPeer peer, AmqpConnectionListener listener, Task<IAmqpConnection> accept) = AmqpTestPeer.StartListener(options, timeout.Token);
        await using AmqpConnectionListener owned = listener;
        await peer.WriteProtocolHeaderAsync(AmqpFrame.SaslProtocolId, timeout.Token);
        await peer.ReadProtocolHeaderAsync(timeout.Token);
        await peer.ReadPerformativeAsync<AmqpSaslMechanismsPerformative>(timeout.Token);

        // Act
        await peer.WriteFrameAsync(0, new AmqpSaslInitPerformative { Mechanism = "X-OVERSIZED" }, timeout.Token, type: AmqpFrame.SaslType);
        AmqpTestFrame answer = await peer.ReadNonEmptyFrameAsync(timeout.Token);
        IReadOnlyList<AmqpPerformative> rest = await peer.ReadToEndAsync(timeout.Token);

        // Assert
        answer.Header.Size.ShouldBeLessThanOrEqualTo(AmqpFrame.MinMaxFrameSize);
        AmqpSaslOutcomePerformative outcome = answer.Performative.ShouldBeOfType<AmqpSaslOutcomePerformative>();
        outcome.Code.ShouldBe(AmqpSaslCode.Sys);
        outcome.AdditionalData.ShouldBeNull();
        rest.ShouldBeEmpty();
        accept.IsCompleted.ShouldBeFalse();
    }

    /// <summary>
    /// Pads a <c>sasl-mechanisms</c> frame that offers ANONYMOUS with filler mechanism names until the
    /// frame is exactly <paramref name="frameSize"/> bytes.
    /// </summary>
    private static AmqpSaslMechanismsPerformative CreateMechanismsOfFrameSize(int frameSize)
    {
        using AmqpBufferWriter writer = new();

        for (int first = 1; first <= 255; first++)
        {
            for (int second = 1; second <= 255; second++)
            {
                AmqpSaslMechanismsPerformative offered = new()
                {
                    Mechanisms = ["ANONYMOUS", new string('A', first), new string('B', second)]
                };

                writer.Reset();
                AmqpFrame.Write(writer, AmqpFrame.SaslType, 0, offered);

                if (writer.Length == frameSize)
                {
                    return offered;
                }
            }
        }

        throw new InvalidOperationException("No filler produces a frame of the requested size.");
    }
}
