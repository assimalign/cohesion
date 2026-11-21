using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Amqp;
using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Amqp.Connections.Internal;

/// <summary>
/// Establishes an AMQP connection over a carrier: the protocol header exchange, the SASL layer when the
/// settings configure one, and the <c>open</c> frames (AMQP 1.0 Part 2, sections 2.2 and 2.4.1; Part 5,
/// section 5.3).
/// </summary>
/// <remarks>
/// <para>
/// Both sides send their <c>open</c> right behind their AMQP protocol header, without waiting for the
/// peer's, as section 2.4.1 permits. The local <c>open</c> must fit the 512 bytes a peer accepts before
/// the connection opens; the peer's <c>open</c> is accepted up to the local maximum frame size, which
/// bounds what this endpoint buffers whatever the peer declares.
/// </para>
/// <para>
/// Every SASL frame, in either direction, is held to those 512 bytes, because SASL negotiates no larger
/// size (Part 5, section 5.3.1). A peer's SASL frame is refused from the size its header declares, before
/// its body arrives; a local one is refused before it is written. A SASL frame carries exactly one value,
/// and an empty one is an irrecoverable error. Credentials too large for a SASL frame, such as bearer
/// tokens, belong on a link after the connection opens.
/// </para>
/// <para>
/// A peer whose protocol header this endpoint does not accept is answered with the header it does accept
/// and refused, as section 2.2 requires. A peer that breaks the protocol after both <c>open</c> frames are
/// on their way is sent <c>close</c> with the error before the carrier is released.
/// </para>
/// <para>
/// Between the AMQP header and the peer's <c>open</c> only AMQP frames are accepted. An empty one on
/// channel 0 is skipped although <c>open</c> should come first, because its meaning is clear; one on
/// another channel is a framing error. The peer's <c>open</c> must declare an idle timeout this endpoint
/// can keep: a shorter one than <see cref="AmqpConnectionSettings.MinRemoteIdleTimeout"/> is refused with
/// <c>close</c> and <c>amqp:not-implemented</c>, as section 2.4.5 allows.
/// </para>
/// </remarks>
internal static class AmqpHandshake
{
    // The protocol headers as big-endian integers: "AMQP", the protocol id, then the version 1.0.0.
    private const ulong amqpHeader = 0x414D_5150_0001_0000;
    private const ulong saslHeader = 0x414D_5150_0301_0000;
    private const uint amqpMagic = 0x414D_5150;

    // Peers drive these failures, so their messages are read once, not on every refused connection.
    private static readonly string _handshakeTimedOutFormat = ErrorMessages.HandshakeTimedOut;
    private static readonly string _handshakeTruncated = ErrorMessages.HandshakeTruncated;
    private static readonly string _protocolHeaderInvalid = ErrorMessages.ProtocolHeaderInvalid;
    private static readonly string _protocolHeaderUnsupportedFormat = ErrorMessages.ProtocolHeaderUnsupported;
    private static readonly string _saslRequired = ErrorMessages.SaslRequired;
    private static readonly string _saslAuthenticationFailedFormat = ErrorMessages.SaslAuthenticationFailed;
    private static readonly string _saslMechanismUnknownFormat = ErrorMessages.SaslMechanismUnknown;
    private static readonly string _peerSaslFrameTooLargeFormat = ErrorMessages.PeerSaslFrameTooLarge;
    private static readonly string _saslFrameEmpty = ErrorMessages.SaslFrameEmpty;
    private static readonly string _saslFrameBodyInvalid = ErrorMessages.SaslFrameBodyInvalid;
    private static readonly string _saslFrameTypeInvalidFormat = ErrorMessages.SaslFrameTypeInvalid;
    private static readonly string _frameTypeUnexpectedFormat = ErrorMessages.FrameTypeUnexpected;
    private static readonly string _channelOutOfRangeFormat = ErrorMessages.ChannelOutOfRange;
    private static readonly string _peerIdleTimeoutTooShortFormat = ErrorMessages.PeerIdleTimeoutTooShort;

    /// <summary>
    /// Establishes an AMQP connection over a carrier within the settings' handshake timeout, and starts it.
    /// </summary>
    /// <param name="carrier">The carrier; it stays the caller's when establishment fails.</param>
    /// <param name="settings">The settings of the listener or factory.</param>
    /// <param name="connecting"><see langword="true"/> on the connecting side; <see langword="false"/> on the listening side.</param>
    /// <param name="hostName">The host name a connecting side names in its <c>open</c> and SASL <c>init</c>.</param>
    /// <param name="cancellationToken">A token to cancel establishment.</param>
    /// <returns>The open, started connection.</returns>
    /// <exception cref="AmqpException">Thrown when the peer's protocol header, SASL exchange or <c>open</c> fails establishment.</exception>
    /// <exception cref="TimeoutException">Thrown when establishment exceeds the handshake timeout.</exception>
    public static async ValueTask<AmqpConnectionEndpoint> EstablishAsync(
        IDuplexConnection carrier,
        AmqpConnectionSettings settings,
        bool connecting,
        string? hostName,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource handshake = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        if (settings.HandshakeTimeout > TimeSpan.Zero)
        {
            handshake.CancelAfter(settings.HandshakeTimeout);
        }

        try
        {
            if (carrier is AmqpMultiplexedCarrier multiplexed)
            {
                await multiplexed.AcquireStreamAsync(handshake.Token).ConfigureAwait(false);
            }

            using AmqpBufferWriter scratch = new(256);

            AmqpConnectionEndpoint connection = connecting
                ? await ConnectAsync(carrier, settings, hostName, scratch, handshake.Token).ConfigureAwait(false)
                : await AcceptAsync(carrier, settings, scratch, handshake.Token).ConfigureAwait(false);

            connection.Start();
            return connection;
        }
        catch (OperationCanceledException exception) when (handshake.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // The timeout fired, not the caller: report a timeout, so neither a factory caller nor a
            // listener's failure event mistakes it for a cancellation someone asked for.
            throw new TimeoutException(
                string.Format(CultureInfo.CurrentCulture, _handshakeTimedOutFormat, settings.HandshakeTimeout),
                exception);
        }
    }

    /// <summary>
    /// Refuses listener settings whose <c>open</c> or <c>sasl-mechanisms</c> frame would exceed the 512 bytes
    /// a peer accepts before the connection opens, so a misconfigured listener fails when it is created
    /// rather than on every connection it accepts.
    /// </summary>
    /// <param name="settings">The listener's settings.</param>
    /// <param name="paramName">The name of the parameter the settings came from.</param>
    /// <exception cref="ArgumentException">Thrown when either frame is too large.</exception>
    public static void ThrowIfListenerFramesTooLarge(AmqpConnectionSettings settings, string paramName)
    {
        using AmqpBufferWriter scratch = new(256);

        if (!TryEncodeFrame(AmqpFrame.AmqpType, settings.CreateOpen(null), scratch))
        {
            throw new ArgumentException(
                string.Format(CultureInfo.CurrentCulture, ErrorMessages.OpenTooLarge, scratch.Length, AmqpFrame.MinMaxFrameSize),
                paramName);
        }

        if (settings.ServerMechanisms.Count > 0)
        {
            AmqpSaslMechanismsPerformative offered = CreateMechanisms(settings.ServerMechanisms);

            if (!TryEncodeFrame(AmqpFrame.SaslType, offered, scratch))
            {
                throw new ArgumentException(
                    string.Format(CultureInfo.CurrentCulture, ErrorMessages.SaslFrameTooLarge, offered.PerformativeName, scratch.Length, AmqpFrame.MinMaxFrameSize),
                    paramName);
            }
        }
    }

    private static async ValueTask<AmqpConnectionEndpoint> ConnectAsync(
        IDuplexConnection carrier,
        AmqpConnectionSettings settings,
        string? hostName,
        AmqpBufferWriter scratch,
        CancellationToken cancellationToken)
    {
        PipeReader input = carrier.Input;
        PipeWriter output = carrier.Output;
        IAmqpSaslConnectionInfo? sasl = null;

        if (settings.ClientMechanism is IAmqpSaslClientMechanism mechanism)
        {
            WriteProtocolHeader(output, saslHeader);
            await FlushAsync(output, cancellationToken).ConfigureAwait(false);

            ulong header = await ReadProtocolHeaderAsync(input, cancellationToken).ConfigureAwait(false);

            if (header != saslHeader)
            {
                throw header == amqpHeader
                    ? new AmqpException(AmqpErrorCode.AuthenticationFailed, ErrorMessages.SaslNotOffered)
                    : CreateHeaderException(header);
            }

            sasl = await AuthenticateAsync(carrier, settings, mechanism, hostName, scratch, cancellationToken).ConfigureAwait(false);
        }

        // The open travels right behind the header (AMQP 1.0 Part 2, section 2.4.1).
        WriteProtocolHeader(output, amqpHeader);
        WriteOpen(output, settings.CreateOpen(hostName), scratch);
        await FlushAsync(output, cancellationToken).ConfigureAwait(false);

        ulong amqp = await ReadProtocolHeaderAsync(input, cancellationToken).ConfigureAwait(false);

        if (amqp != amqpHeader)
        {
            // A SASL header asks for authentication only when none took place; after it, the header is just
            // one this endpoint does not accept.
            throw amqp == saslHeader && sasl is null
                ? new AmqpException(AmqpErrorCode.AuthenticationFailed, ErrorMessages.SaslAuthenticationRequired)
                : CreateHeaderException(amqp);
        }

        AmqpOpenPerformative remoteOpen = await ReadOpenAsync(input, output, settings, scratch, cancellationToken).ConfigureAwait(false);

        return new AmqpConnectionEndpoint(carrier, settings, remoteOpen, sasl);
    }

    private static async ValueTask<AmqpConnectionEndpoint> AcceptAsync(
        IDuplexConnection carrier,
        AmqpConnectionSettings settings,
        AmqpBufferWriter scratch,
        CancellationToken cancellationToken)
    {
        PipeReader input = carrier.Input;
        PipeWriter output = carrier.Output;
        IAmqpSaslConnectionInfo? sasl = null;

        ulong header = await ReadProtocolHeaderAsync(input, cancellationToken).ConfigureAwait(false);

        if (settings.ServerMechanisms.Count > 0)
        {
            if (header != saslHeader)
            {
                // Answer with the header this endpoint requires, then refuse (AMQP 1.0 Part 2, section 2.2).
                await RefuseAsync(output, saslHeader, cancellationToken).ConfigureAwait(false);

                throw header == amqpHeader
                    ? new AmqpException(AmqpErrorCode.AuthenticationFailed, _saslRequired)
                    : CreateHeaderException(header);
            }

            WriteProtocolHeader(output, saslHeader);
            sasl = await AuthenticatePeerAsync(carrier, settings, scratch, cancellationToken).ConfigureAwait(false);
            header = await ReadProtocolHeaderAsync(input, cancellationToken).ConfigureAwait(false);
        }

        if (header != amqpHeader)
        {
            await RefuseAsync(output, amqpHeader, cancellationToken).ConfigureAwait(false);

            throw CreateHeaderException(header);
        }

        WriteProtocolHeader(output, amqpHeader);
        WriteOpen(output, settings.CreateOpen(null), scratch);
        await FlushAsync(output, cancellationToken).ConfigureAwait(false);

        AmqpOpenPerformative remoteOpen = await ReadOpenAsync(input, output, settings, scratch, cancellationToken).ConfigureAwait(false);

        return new AmqpConnectionEndpoint(carrier, settings, remoteOpen, sasl);
    }

    /// <summary>
    /// Runs the connecting side of the SASL exchange, after both SASL headers (AMQP 1.0 Part 5, section 5.3.2).
    /// </summary>
    private static async ValueTask<IAmqpSaslConnectionInfo> AuthenticateAsync(
        IDuplexConnection carrier,
        AmqpConnectionSettings settings,
        IAmqpSaslClientMechanism mechanism,
        string? hostName,
        AmqpBufferWriter scratch,
        CancellationToken cancellationToken)
    {
        PipeReader input = carrier.Input;
        PipeWriter output = carrier.Output;

        AmqpPerformative first = await ReadSaslFrameAsync(input, cancellationToken).ConfigureAwait(false);

        if (first is not AmqpSaslMechanismsPerformative offered)
        {
            throw CreateSaslFrameException(first, "sasl-mechanisms");
        }

        if (!Contains(offered.Mechanisms, mechanism.Name))
        {
            throw new AmqpException(
                AmqpErrorCode.AuthenticationFailed,
                string.Format(CultureInfo.CurrentCulture, ErrorMessages.SaslMechanismNotOffered, mechanism.Name));
        }

        IAmqpSaslClientExchange exchange = mechanism.CreateExchange(carrier, hostName);
        ReadOnlyMemory<byte>? initialResponse = await exchange.GetInitialResponseAsync(cancellationToken).ConfigureAwait(false);

        WriteSaslFrame(output, new AmqpSaslInitPerformative
        {
            Mechanism = mechanism.Name,
            InitialResponse = initialResponse,
            HostName = hostName
        }, scratch);
        await FlushAsync(output, cancellationToken).ConfigureAwait(false);

        while (true)
        {
            AmqpPerformative frame = await ReadSaslFrameAsync(input, cancellationToken).ConfigureAwait(false);

            switch (frame)
            {
                case AmqpSaslChallengePerformative challenge:
                    ReadOnlyMemory<byte> response = await exchange.RespondAsync(challenge.Challenge, cancellationToken).ConfigureAwait(false);

                    WriteSaslFrame(output, new AmqpSaslResponsePerformative { Response = response }, scratch);
                    await FlushAsync(output, cancellationToken).ConfigureAwait(false);
                    break;
                case AmqpSaslOutcomePerformative outcome:
                    if (outcome.Code != AmqpSaslCode.Ok)
                    {
                        throw new AmqpException(
                            AmqpErrorCode.AuthenticationFailed,
                            string.Format(CultureInfo.CurrentCulture, _saslAuthenticationFailedFormat, outcome.Code));
                    }

                    ReadOnlyMemory<byte> additionalData = outcome.AdditionalData ?? ReadOnlyMemory<byte>.Empty;

                    await exchange.CompleteAsync(additionalData, cancellationToken).ConfigureAwait(false);

                    return new AmqpSaslConnectionInfo(mechanism.Name, hostName, null, additionalData);
                default:
                    throw CreateSaslFrameException(frame, "sasl-outcome");
            }
        }
    }

    /// <summary>
    /// Runs the listening side of the SASL exchange, after the peer's SASL header and before the local one
    /// is flushed (AMQP 1.0 Part 5, section 5.3.2).
    /// </summary>
    private static async ValueTask<IAmqpSaslConnectionInfo> AuthenticatePeerAsync(
        IDuplexConnection carrier,
        AmqpConnectionSettings settings,
        AmqpBufferWriter scratch,
        CancellationToken cancellationToken)
    {
        PipeReader input = carrier.Input;
        PipeWriter output = carrier.Output;
        IReadOnlyList<IAmqpSaslServerMechanism> mechanisms = settings.ServerMechanisms;

        WriteSaslFrame(output, CreateMechanisms(mechanisms), scratch);
        await FlushAsync(output, cancellationToken).ConfigureAwait(false);

        AmqpPerformative first = await ReadSaslFrameAsync(input, cancellationToken).ConfigureAwait(false);

        if (first is not AmqpSaslInitPerformative init)
        {
            throw CreateSaslFrameException(first, "sasl-init");
        }

        IAmqpSaslServerMechanism? mechanism = null;

        foreach (IAmqpSaslServerMechanism candidate in mechanisms)
        {
            if (candidate.Name == init.Mechanism)
            {
                mechanism = candidate;
                break;
            }
        }

        if (mechanism is null)
        {
            await WriteMechanismFrameAsync(output, CreateOutcome(AmqpSaslCode.Auth, ReadOnlyMemory<byte>.Empty), scratch, cancellationToken).ConfigureAwait(false);

            throw new AmqpException(
                AmqpErrorCode.AuthenticationFailed,
                string.Format(CultureInfo.CurrentCulture, _saslMechanismUnknownFormat, init.Mechanism));
        }

        AmqpSaslServerStep step;

        try
        {
            IAmqpSaslServerExchange exchange = mechanism.CreateExchange(carrier, init.HostName);

            step = await exchange.EvaluateAsync(init.InitialResponse, cancellationToken).ConfigureAwait(false);

            while (!step.IsComplete)
            {
                await WriteMechanismFrameAsync(output, new AmqpSaslChallengePerformative { Challenge = step.Data }, scratch, cancellationToken).ConfigureAwait(false);

                AmqpPerformative frame = await ReadSaslFrameAsync(input, cancellationToken).ConfigureAwait(false);

                if (frame is not AmqpSaslResponsePerformative response)
                {
                    throw CreateSaslFrameException(frame, "sasl-response");
                }

                step = await exchange.EvaluateAsync(response.Response, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is not (AmqpException or OperationCanceledException))
        {
            // Justified catch-all: a mechanism is application code, and whatever it throws still owes the
            // peer an outcome rather than a dropped connection. The exception propagates unchanged.
            await TryWriteOutcomeAsync(output, AmqpSaslCode.Sys, scratch).ConfigureAwait(false);
            throw;
        }

        AmqpSaslCode code = step.Code.GetValueOrDefault();
        ReadOnlyMemory<byte> additionalData = code == AmqpSaslCode.Ok ? step.Data : ReadOnlyMemory<byte>.Empty;

        await WriteMechanismFrameAsync(output, CreateOutcome(code, additionalData), scratch, cancellationToken).ConfigureAwait(false);

        if (code != AmqpSaslCode.Ok)
        {
            throw new AmqpException(
                AmqpErrorCode.AuthenticationFailed,
                string.Format(CultureInfo.CurrentCulture, _saslAuthenticationFailedFormat, code));
        }

        return new AmqpSaslConnectionInfo(mechanism.Name, init.HostName, step.Identity, additionalData);
    }

    /// <summary>
    /// Reads the peer's <c>open</c>, which must be the first frame after its AMQP header and arrive on
    /// channel 0, and refuses an idle timeout too short to keep. A violation or refusal is answered with
    /// <c>close</c>, since the local <c>open</c> is already sent.
    /// </summary>
    private static async ValueTask<AmqpOpenPerformative> ReadOpenAsync(
        PipeReader input,
        PipeWriter output,
        AmqpConnectionSettings settings,
        AmqpBufferWriter scratch,
        CancellationToken cancellationToken)
    {
        try
        {
            (AmqpFrameHeader header, AmqpPerformative performative) = await ReadFrameAsync(input, settings.MaxFrameSize, AmqpFrame.AmqpType, cancellationToken).ConfigureAwait(false);

            if (performative is not AmqpOpenPerformative open)
            {
                throw AmqpException.ProtocolViolation(
                    AmqpErrorCondition.IllegalState,
                    string.Format(CultureInfo.CurrentCulture, ErrorMessages.OpenExpected, performative.PerformativeName));
            }

            if (header.Channel != 0)
            {
                throw AmqpException.FramingError(
                    string.Format(CultureInfo.CurrentCulture, ErrorMessages.OpenChannelInvalid, header.Channel));
            }

            // Zero means no idle timeout, as an absent one does. A shorter one than this endpoint can keep is
            // refused with the reason rather than kept late (AMQP 1.0 Part 2, section 2.4.5).
            if (open.IdleTimeout is uint idleTimeout and > 0 and < AmqpConnectionSettings.MinRemoteIdleTimeout)
            {
                string message = string.Format(
                    CultureInfo.CurrentCulture,
                    _peerIdleTimeoutTooShortFormat,
                    idleTimeout,
                    AmqpConnectionSettings.MinRemoteIdleTimeout);

                throw new AmqpException(AmqpErrorCode.Other, message)
                {
                    Error = new AmqpError(AmqpErrorCondition.NotImplemented, message)
                };
            }

            return open;
        }
        catch (AmqpException exception) when (exception.Error is not null)
        {
            await TryWriteCloseAsync(output, exception.Error, scratch).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Reads the next SASL frame. AMQP frames are not allowed until the SASL exchange completes, and no SASL
    /// frame may exceed the minimum maximum frame size (AMQP 1.0 Part 5, section 5.3.1).
    /// </summary>
    private static async ValueTask<AmqpPerformative> ReadSaslFrameAsync(PipeReader input, CancellationToken cancellationToken)
    {
        (_, AmqpPerformative performative) = await ReadFrameAsync(input, AmqpFrame.MinMaxFrameSize, AmqpFrame.SaslType, cancellationToken).ConfigureAwait(false);

        return performative;
    }

    /// <summary>
    /// Reads the next frame with a body and leaves every byte after it in the pipe. A frame of another type
    /// than <paramref name="type"/> is refused.
    /// </summary>
    /// <remarks>
    /// During the SASL exchange an empty frame is an irrecoverable error (AMQP 1.0 Part 5, section 5.3.1).
    /// Before the peer's <c>open</c>, an empty AMQP frame breaks the rule that <c>open</c> comes first
    /// (Part 2, section 2.4.1), but its meaning is clear, so one on channel 0 is skipped as keep-alive
    /// traffic, as other implementations do; the handshake timeout still bounds the wait for the
    /// <c>open</c>. One on another channel is a framing error: until the limits are negotiated the
    /// highest channel is 0.
    /// </remarks>
    private static async ValueTask<(AmqpFrameHeader Header, AmqpPerformative Performative)> ReadFrameAsync(
        PipeReader input,
        uint maxFrameSize,
        byte type,
        CancellationToken cancellationToken)
    {
        bool sasl = type == AmqpFrame.SaslType;

        while (true)
        {
            ReadResult result = await input.ReadAsync(cancellationToken).ConfigureAwait(false);
            ReadOnlySequence<byte> buffer = result.Buffer;
            SequencePosition consumed = buffer.Start;
            SequencePosition examined = buffer.End;

            try
            {
                while (true)
                {
                    // A SASL frame is refused from the size it declares, before its body arrives, with a message
                    // that names the SASL limit rather than the local maximum frame size.
                    if (sasl && AmqpFrame.TryReadSize(buffer, out uint size) && size > AmqpFrame.MinMaxFrameSize)
                    {
                        throw new AmqpException(
                            AmqpErrorCode.ProtocolViolation,
                            string.Format(CultureInfo.CurrentCulture, _peerSaslFrameTooLargeFormat, size, AmqpFrame.MinMaxFrameSize));
                    }

                    if (!AmqpFrame.TryRead(ref buffer, maxFrameSize, out AmqpFrameHeader header, out ReadOnlySequence<byte> body))
                    {
                        break;
                    }

                    consumed = buffer.Start;

                    if (header.Type != type)
                    {
                        throw sasl
                            ? new AmqpException(AmqpErrorCode.ProtocolViolation, string.Format(CultureInfo.CurrentCulture, _saslFrameTypeInvalidFormat, header.Type))
                            : AmqpException.FramingError(string.Format(CultureInfo.CurrentCulture, _frameTypeUnexpectedFormat, header.Type));
                    }

                    if (body.IsEmpty)
                    {
                        if (sasl)
                        {
                            throw new AmqpException(AmqpErrorCode.ProtocolViolation, _saslFrameEmpty);
                        }

                        if (header.Channel != 0)
                        {
                            throw AmqpException.FramingError(string.Format(CultureInfo.CurrentCulture, _channelOutOfRangeFormat, header.Channel, 0));
                        }

                        continue;
                    }

                    // Bytes behind the frame belong to the next reader, which must see them without new data.
                    examined = consumed;
                    return (header, Decode(body, sasl));
                }

                if (result.IsCompleted || result.IsCanceled)
                {
                    throw new AmqpException(AmqpErrorCode.ConnectionClosed, _handshakeTruncated);
                }
            }
            finally
            {
                input.AdvanceTo(consumed, examined);
            }
        }
    }

    private static async ValueTask<ulong> ReadProtocolHeaderAsync(PipeReader input, CancellationToken cancellationToken)
    {
        while (true)
        {
            ReadResult result = await input.ReadAsync(cancellationToken).ConfigureAwait(false);
            ReadOnlySequence<byte> buffer = result.Buffer;

            if (TryReadProtocolHeader(ref buffer, out ulong header))
            {
                input.AdvanceTo(buffer.Start);
                return header;
            }

            input.AdvanceTo(buffer.Start, buffer.End);

            if (result.IsCompleted || result.IsCanceled)
            {
                throw new AmqpException(AmqpErrorCode.ConnectionClosed, _handshakeTruncated);
            }
        }
    }

    private static bool TryReadProtocolHeader(ref ReadOnlySequence<byte> buffer, out ulong header)
    {
        if (buffer.Length < AmqpFrame.ProtocolHeaderSize)
        {
            header = 0;
            return false;
        }

        Span<byte> bytes = stackalloc byte[AmqpFrame.ProtocolHeaderSize];

        buffer.Slice(0, AmqpFrame.ProtocolHeaderSize).CopyTo(bytes);
        header = BinaryPrimitives.ReadUInt64BigEndian(bytes);
        buffer = buffer.Slice(AmqpFrame.ProtocolHeaderSize);
        return true;
    }

    /// <summary>
    /// Decodes a frame body's performative. A SASL frame body holds exactly that one value (AMQP 1.0 Part 5,
    /// section 5.3.1); an AMQP frame may carry a payload behind it, which the <c>open</c> ignores.
    /// </summary>
    private static AmqpPerformative Decode(ReadOnlySequence<byte> body, bool sasl)
    {
        ReadOnlySpan<byte> span = body.IsSingleSegment ? body.FirstSpan : body.ToArray();
        AmqpReader reader = new(span);
        AmqpPerformative performative = AmqpPerformative.Decode(ref reader);

        if (sasl && !reader.IsEnd)
        {
            throw new AmqpException(AmqpErrorCode.ProtocolViolation, _saslFrameBodyInvalid);
        }

        return performative;
    }

    private static AmqpException CreateHeaderException(ulong header)
    {
        if ((uint)(header >> 32) != amqpMagic)
        {
            return new AmqpException(AmqpErrorCode.ProtocolViolation, _protocolHeaderInvalid);
        }

        return new AmqpException(
            AmqpErrorCode.ProtocolViolation,
            string.Format(
                CultureInfo.CurrentCulture,
                _protocolHeaderUnsupportedFormat,
                (byte)(header >> 24),
                (byte)(header >> 16),
                (byte)(header >> 8),
                (byte)header));
    }

    private static AmqpException CreateSaslFrameException(AmqpPerformative performative, string expected)
    {
        return new AmqpException(
            AmqpErrorCode.ProtocolViolation,
            string.Format(CultureInfo.CurrentCulture, ErrorMessages.SaslFrameUnexpected, performative.PerformativeName, expected));
    }

    private static bool Contains(IReadOnlyList<AmqpSymbol> mechanisms, AmqpSymbol name)
    {
        foreach (AmqpSymbol mechanism in mechanisms)
        {
            if (mechanism == name)
            {
                return true;
            }
        }

        return false;
    }

    private static void WriteProtocolHeader(PipeWriter output, ulong header)
    {
        Span<byte> span = output.GetSpan(AmqpFrame.ProtocolHeaderSize);

        BinaryPrimitives.WriteUInt64BigEndian(span, header);
        output.Advance(AmqpFrame.ProtocolHeaderSize);
    }

    private static void WriteOpen(PipeWriter output, AmqpOpenPerformative open, AmqpBufferWriter scratch)
    {
        // Until the peer's open arrives, no frame may exceed the minimum maximum frame size (section 2.4.1).
        if (!TryEncodeFrame(AmqpFrame.AmqpType, open, scratch))
        {
            throw new AmqpException(
                AmqpErrorCode.Other,
                string.Format(CultureInfo.CurrentCulture, ErrorMessages.OpenTooLarge, scratch.Length, AmqpFrame.MinMaxFrameSize));
        }

        output.Write(scratch.WrittenSpan);
    }

    /// <summary>
    /// Writes a SASL frame, refusing one larger than the minimum maximum frame size.
    /// </summary>
    /// <exception cref="AmqpException">Thrown when the frame exceeds the minimum maximum frame size.</exception>
    private static void WriteSaslFrame(PipeWriter output, AmqpPerformative performative, AmqpBufferWriter scratch)
    {
        if (!TryEncodeFrame(AmqpFrame.SaslType, performative, scratch))
        {
            throw CreateSaslFrameTooLargeException(performative, scratch.Length);
        }

        output.Write(scratch.WrittenSpan);
    }

    /// <summary>
    /// Writes and flushes a SASL frame whose content the listening side's mechanism chose: a challenge or an
    /// outcome. One too large for a SASL frame is answered with a system outcome instead, so the peer learns
    /// that authentication ended rather than losing the connection without a word.
    /// </summary>
    /// <exception cref="AmqpException">Thrown when the frame exceeds the minimum maximum frame size.</exception>
    private static async ValueTask WriteMechanismFrameAsync(
        PipeWriter output,
        AmqpPerformative performative,
        AmqpBufferWriter scratch,
        CancellationToken cancellationToken)
    {
        if (!TryEncodeFrame(AmqpFrame.SaslType, performative, scratch))
        {
            AmqpException exception = CreateSaslFrameTooLargeException(performative, scratch.Length);

            await TryWriteOutcomeAsync(output, AmqpSaslCode.Sys, scratch).ConfigureAwait(false);
            throw exception;
        }

        output.Write(scratch.WrittenSpan);
        await FlushAsync(output, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Encodes a frame on channel 0 into the scratch buffer and reports whether it fits the minimum maximum
    /// frame size, the limit before the peer's <c>open</c> arrives and on every SASL frame.
    /// </summary>
    private static bool TryEncodeFrame(byte type, AmqpPerformative performative, AmqpBufferWriter scratch)
    {
        scratch.Reset();
        AmqpFrame.Write(scratch, type, 0, performative);
        return scratch.Length <= AmqpFrame.MinMaxFrameSize;
    }

    private static AmqpSaslMechanismsPerformative CreateMechanisms(IReadOnlyList<IAmqpSaslServerMechanism> mechanisms)
    {
        AmqpSymbol[] names = new AmqpSymbol[mechanisms.Count];

        for (int index = 0; index < names.Length; index++)
        {
            names[index] = mechanisms[index].Name;
        }

        return new AmqpSaslMechanismsPerformative { Mechanisms = names };
    }

    private static AmqpSaslOutcomePerformative CreateOutcome(AmqpSaslCode code, ReadOnlyMemory<byte> additionalData)
    {
        // The cast matters: a bare null would convert through byte[] to an empty memory, and the field
        // would travel as an empty binary instead of being left out.
        return new AmqpSaslOutcomePerformative
        {
            Code = code,
            AdditionalData = additionalData.IsEmpty ? (ReadOnlyMemory<byte>?)null : additionalData
        };
    }

    private static AmqpException CreateSaslFrameTooLargeException(AmqpPerformative performative, int size)
    {
        return new AmqpException(
            AmqpErrorCode.Other,
            string.Format(CultureInfo.CurrentCulture, ErrorMessages.SaslFrameTooLarge, performative.PerformativeName, size, AmqpFrame.MinMaxFrameSize));
    }

    private static async ValueTask TryWriteOutcomeAsync(PipeWriter output, AmqpSaslCode code, AmqpBufferWriter scratch)
    {
        try
        {
            // An outcome without additional data always fits a SASL frame.
            TryEncodeFrame(AmqpFrame.SaslType, CreateOutcome(code, ReadOnlyMemory<byte>.Empty), scratch);
            output.Write(scratch.WrittenSpan);
            await FlushAsync(output, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is AmqpException or InvalidOperationException or System.IO.IOException or ObjectDisposedException)
        {
            // The outcome is a courtesy: the carrier is released whether or not it arrives.
        }
    }

    private static async ValueTask TryWriteCloseAsync(PipeWriter output, AmqpError error, AmqpBufferWriter scratch)
    {
        try
        {
            // The peer's open has not arrived, so the close must fit the minimum maximum frame size. A
            // description too long for it is left out; the condition still travels.
            if (!TryEncodeFrame(AmqpFrame.AmqpType, new AmqpClosePerformative { Error = error }, scratch))
            {
                TryEncodeFrame(AmqpFrame.AmqpType, new AmqpClosePerformative { Error = new AmqpError(error.Condition) }, scratch);
            }

            output.Write(scratch.WrittenSpan);
            await FlushAsync(output, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is AmqpException or InvalidOperationException or System.IO.IOException or ObjectDisposedException)
        {
            // The close is a courtesy: the carrier is released whether or not it arrives.
        }
    }

    private static async ValueTask RefuseAsync(PipeWriter output, ulong header, CancellationToken cancellationToken)
    {
        WriteProtocolHeader(output, header);

        try
        {
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.IO.IOException or ObjectDisposedException)
        {
            // The answer is a courtesy: the refusal stands whether or not it arrives.
        }
    }

    private static async ValueTask FlushAsync(PipeWriter output, CancellationToken cancellationToken)
    {
        FlushResult result = await output.FlushAsync(cancellationToken).ConfigureAwait(false);

        if (result.IsCompleted || result.IsCanceled)
        {
            throw new AmqpException(AmqpErrorCode.ConnectionClosed, _handshakeTruncated);
        }
    }
}
