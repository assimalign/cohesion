using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

using Shouldly;

using Assimalign.Cohesion.Amqp;
using Assimalign.Cohesion.Amqp.Connections.Internal;
using Assimalign.Cohesion.Connections;

using static Assimalign.Cohesion.Amqp.Connections.Tests.AmqpHeaderPhaseStep;

namespace Assimalign.Cohesion.Amqp.Connections.Tests;

/// <summary>
/// The header-phase compliance corpus: scripted establishments of AMQP 1.0 connections, from the protocol
/// header through the SASL exchange and the <c>open</c> frames to the keep-alive traffic after them, each
/// tied to the rule of Part 2, sections 2.2 to 2.4, or Part 5, section 5.3 it exercises.
/// </summary>
/// <remarks>
/// <para>
/// A case runs against a listener, where the scripted peer is the client, or a factory, where it is the
/// server. Its script is the bytes the peer writes and what it expects back, so a case reads as the wire
/// exchange it pins; frames are built with the library's codec, which its own corpus covers.
/// </para>
/// <para>
/// A case that fails names the exact message and, where the engine sends <c>close</c>, the exact condition
/// and description, so a rule that starts refusing for the wrong reason fails as surely as one that stops
/// refusing. Add a case with every establishment a peer is found to attempt that the engine gets wrong.
/// </para>
/// </remarks>
internal static class AmqpHeaderPhaseCorpus
{
    private const byte sasl = AmqpFrame.SaslType;

    private static readonly TimeSpan _keepAliveWindow = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan _silence = TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// Gets the AMQP protocol header, version 1.0.0.
    /// </summary>
    public static byte[] AmqpHeader { get; } = Header(AmqpFrame.AmqpProtocolId);

    /// <summary>
    /// Gets the SASL protocol header, version 1.0.0.
    /// </summary>
    public static byte[] SaslHeader { get; } = Header(AmqpFrame.SaslProtocolId);

    /// <summary>
    /// Gets the rules the corpus exercises.
    /// </summary>
    public static IReadOnlyList<AmqpHeaderPhaseRule> Rules { get; } =
    [
        new("H1", "A peer starts with the header of the layer it opens: the SASL header when it authenticates, the AMQP header otherwise, and the AMQP header again once SASL succeeds.", "Part 2, section 2.2; Part 5, sections 5.1 and 5.3", false),
        new("H2", "A server that does not support the requested version answers with the header it supports and closes the socket.", "Part 2, section 2.2", true),
        new("H3", "The protocol id is not part of the version: a server answers a protocol id it does not accept with the header it requires, then closes the socket.", "Part 2, section 2.2; Part 5, sections 5.2 and 5.3", true),
        new("H4", "Bytes that are not an AMQP protocol header are answered with a valid header before the socket closes.", "Part 2, section 2.2", true),
        new("H5", "A client whose header the server answers with another one fails before any frame is exchanged.", "Part 2, section 2.2; Part 5, section 5.3", true),
        new("H6", "In the SASL exchange the server sends sasl-mechanisms, the client sasl-init, then zero or more sasl-challenge and sasl-response, and the server sasl-outcome; any other performative is refused.", "Part 5, sections 5.3.2 and 5.3.3", true),
        new("H7", "Only SASL frames travel during the SASL exchange.", "Part 5, section 5.3.1", true),
        new("H8", "A SASL frame holds exactly one SASL performative and is never empty.", "Part 5, section 5.3.1", true),
        new("H9", "A SASL frame is at most 512 bytes, refused from its header, and its channel bytes and extended header are ignored.", "Part 5, section 5.3.1", true),
        new("H10", "Once SASL succeeds both peers continue with the AMQP header, and anything else is answered with it and refused.", "Part 5, section 5.3.2; Part 2, section 2.2", true),
        new("H11", "A sasl-outcome other than ok ends the connection, as does a mechanism the server does not offer.", "Part 5, sections 5.3.2, 5.3.3.2 and 5.3.3.6", true),
        new("H12", "Each peer sends open first after the AMQP header; any other frame in its place is refused with close.", "Part 2, sections 2.3.1 and 2.4.1", true),
        new("H13", "The open frame travels on channel 0.", "Part 2, section 2.4.1", true),
        new("H14", "Before its open a peer should send nothing else, but an empty frame on channel 0 only keeps the connection alive, so it is skipped; on another channel it is a framing error, the highest channel being 0 until the limits are negotiated.", "Part 2, sections 2.4.1 and 2.4.5", true, Leniency: true),
        new("H15", "A peer may pipeline its header, its open and further frames without waiting for its partner's.", "Part 2, section 2.4.2", false),
        new("H16", "The open declares the idle timeout its sender requires, half its own threshold; a peer keeps its partner's with empty frames on channel 0 when it has nothing else to send, none when its partner declares none, and none after its close.", "Part 2, sections 2.4.5 and 2.7.1", false),
        new("H17", "A peer that cannot keep the idle timeout its partner declares closes the connection with the reason; this engine keeps 100 ms and up.", "Part 2, sections 2.4.5 and 2.7.1", true),
        new("H18", "Empty frames are taken on any channel the receiver accepts; a channel above its channel-max, a close's included, is a framing error, and only AMQP frames, empty or not, travel once the connection is open. A close that answers the receiver's own ends the connection whatever its channel, since refusing it would only hold the socket.", "Part 2, sections 2.3.1, 2.4.3, 2.4.5 and 2.7.1", true),
        new("H19", "A frame is at least 8 bytes with a data offset of at least 2, and an AMQP frame's extended header is ignored.", "Part 2, sections 2.3.1 and 2.3.2", true)
    ];

    /// <summary>
    /// Gets the cases.
    /// </summary>
    public static IReadOnlyList<AmqpHeaderPhaseCase> Cases { get; } = CreateCases();

    /// <summary>
    /// Finds the case with the name.
    /// </summary>
    public static AmqpHeaderPhaseCase Find(string name)
    {
        return Cases.Single(entry => entry.Name == name);
    }

    /// <summary>
    /// Finds the rule with the identifier.
    /// </summary>
    public static AmqpHeaderPhaseRule FindRule(string id)
    {
        return Rules.Single(rule => rule.Id == id);
    }

    /// <summary>
    /// Creates a protocol header: <c>AMQP</c>, the protocol id, then the version.
    /// </summary>
    public static byte[] Header(byte protocolId, byte major = 1, byte minor = 0, byte revision = 0)
    {
        return [(byte)'A', (byte)'M', (byte)'Q', (byte)'P', protocolId, major, minor, revision];
    }

    /// <summary>
    /// Creates a frame around a performative, or an empty frame, with an extended header of
    /// <paramref name="extendedWords"/> four-byte words.
    /// </summary>
    public static byte[] Frame(AmqpPerformative? performative, ushort channel = 0, byte type = AmqpFrame.AmqpType, int extendedWords = 0)
    {
        return FrameWithBody(performative is null ? [] : Encode(performative), channel, type, extendedWords);
    }

    /// <summary>
    /// Creates a frame around a body encoded by hand.
    /// </summary>
    public static byte[] FrameWithBody(byte[] body, ushort channel = 0, byte type = AmqpFrame.AmqpType, int extendedWords = 0)
    {
        int offset = AmqpFrame.HeaderSize + extendedWords * 4;
        byte[] frame = new byte[offset + body.Length];

        BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)frame.Length);
        frame[4] = (byte)(offset / 4);
        frame[5] = type;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(6), channel);
        body.CopyTo(frame, offset);
        return frame;
    }

    /// <summary>
    /// Creates a frame header that declares whatever it is told to, including sizes and data offsets that
    /// break the frame layout.
    /// </summary>
    public static byte[] FrameHeader(uint size, byte dataOffset, byte type = AmqpFrame.AmqpType, ushort channel = 0)
    {
        byte[] header = new byte[AmqpFrame.HeaderSize];

        BinaryPrimitives.WriteUInt32BigEndian(header, size);
        header[4] = dataOffset;
        header[5] = type;
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(6), channel);
        return header;
    }

    private static byte[] Encode(AmqpPerformative performative)
    {
        using AmqpBufferWriter buffer = new();

        AmqpFrame.Write(buffer, AmqpFrame.AmqpType, 0, performative);
        return buffer.ToArray()[AmqpFrame.HeaderSize..];
    }

    private static AmqpOpenPerformative Open(uint? idleTimeout = null)
    {
        AmqpOpenPerformative open = AmqpTestPeer.CreateOpen();

        open.IdleTimeout = idleTimeout;
        return open;
    }

    private static AmqpSaslMechanismsPerformative Mechanisms(string mechanism)
    {
        return new AmqpSaslMechanismsPerformative { Mechanisms = [mechanism] };
    }

    private static AmqpSaslInitPerformative SaslInit(string mechanism)
    {
        return new AmqpSaslInitPerformative { Mechanism = mechanism };
    }

    private static AmqpSaslChallengePerformative Challenge(string challenge)
    {
        return new AmqpSaslChallengePerformative { Challenge = Encoding.ASCII.GetBytes(challenge) };
    }

    private static AmqpSaslResponsePerformative Response(string response)
    {
        return new AmqpSaslResponsePerformative { Response = Encoding.ASCII.GetBytes(response) };
    }

    private static AmqpSaslOutcomePerformative Outcome(AmqpSaslCode code, string? additionalData = null)
    {
        return new AmqpSaslOutcomePerformative
        {
            Code = code,
            // The cast matters: a bare null would convert through byte[] to an empty binary.
            AdditionalData = additionalData is null ? (ReadOnlyMemory<byte>?)null : Encoding.ASCII.GetBytes(additionalData)
        };
    }

    private static string Format(string format, params object[] arguments)
    {
        return string.Format(CultureInfo.CurrentCulture, format, arguments);
    }

    private static string Unsupported(byte protocolId, byte major, byte minor, byte revision)
    {
        return Format(ErrorMessages.ProtocolHeaderUnsupported, protocolId, major, minor, revision);
    }

    private static Action<AmqpPerformative> OutcomeIs(AmqpSaslCode code)
    {
        return performative => performative.ShouldBeOfType<AmqpSaslOutcomePerformative>().Code.ShouldBe(code);
    }

    private static Action<IAmqpConnection> AuthenticatedWith(string mechanism)
    {
        return connection => connection.Features.Get<IAmqpSaslConnectionInfo>().ShouldNotBeNull().Mechanism.ShouldBe((AmqpSymbol)mechanism);
    }

    private static Action<IAmqpConnection> KeepsIdleTimeout(int milliseconds)
    {
        return connection => connection.RemoteIdleTimeout.ShouldBe(TimeSpan.FromMilliseconds(milliseconds));
    }

    /// <summary>
    /// The client opens without SASL: its header and open, then the listener's header and open.
    /// </summary>
    private static AmqpHeaderPhaseStep[] ClientOpens(AmqpOpenPerformative? open = null)
    {
        return
        [
            Write(AmqpHeader, Frame(open ?? Open())),
            ExpectHeader(AmqpHeader),
            ExpectFrame("open")
        ];
    }

    /// <summary>
    /// The server answers the factory's header and open with its own.
    /// </summary>
    private static AmqpHeaderPhaseStep[] ServerOpens(AmqpOpenPerformative? open = null)
    {
        return
        [
            ExpectHeader(AmqpHeader),
            ExpectFrame("open"),
            Write(AmqpHeader, Frame(open ?? Open()))
        ];
    }

    /// <summary>
    /// The client authenticates against a listener with <c>ANONYMOUS</c>, up to the outcome.
    /// </summary>
    private static AmqpHeaderPhaseStep[] ClientAuthenticates()
    {
        return
        [
            Write(SaslHeader),
            ExpectHeader(SaslHeader),
            ExpectFrame("sasl-mechanisms"),
            Write(Frame(SaslInit("ANONYMOUS"), type: sasl)),
            ExpectFrame("sasl-outcome", OutcomeIs(AmqpSaslCode.Ok))
        ];
    }

    /// <summary>
    /// The server authenticates a factory configured with <c>ANONYMOUS</c>, up to the outcome.
    /// </summary>
    private static AmqpHeaderPhaseStep[] ServerAuthenticates()
    {
        return
        [
            ExpectHeader(SaslHeader),
            Write(SaslHeader, Frame(Mechanisms("ANONYMOUS"), type: sasl)),
            ExpectFrame("sasl-init"),
            Write(Frame(Outcome(AmqpSaslCode.Ok), type: sasl))
        ];
    }

    /// <summary>
    /// The client starts a SASL exchange with a listener and reads the mechanisms it offers.
    /// </summary>
    private static AmqpHeaderPhaseStep[] ClientStartsSasl()
    {
        return
        [
            Write(SaslHeader),
            ExpectHeader(SaslHeader),
            ExpectFrame("sasl-mechanisms")
        ];
    }

    private static AmqpHeaderPhaseCase Listener(string name, string rule, AmqpHeaderPhaseSasl mechanism, AmqpHeaderPhaseStep[] script, AmqpHeaderPhaseOutcome outcome)
    {
        return new AmqpHeaderPhaseCase(name, rule, AmqpHeaderPhaseRole.Listener, mechanism, script, outcome);
    }

    private static AmqpHeaderPhaseCase Factory(string name, string rule, AmqpHeaderPhaseSasl mechanism, AmqpHeaderPhaseStep[] script, AmqpHeaderPhaseOutcome outcome)
    {
        return new AmqpHeaderPhaseCase(name, rule, AmqpHeaderPhaseRole.Factory, mechanism, script, outcome);
    }

    private static AmqpHeaderPhaseOutcome Refused(string message)
    {
        return AmqpHeaderPhaseOutcome.Fail(AmqpErrorCode.ProtocolViolation, message);
    }

    private static AmqpHeaderPhaseOutcome Closed(AmqpSymbol condition, string message)
    {
        return AmqpHeaderPhaseOutcome.Fail(AmqpErrorCode.ProtocolViolation, message, condition);
    }

    private static AmqpHeaderPhaseOutcome Unauthenticated(string message)
    {
        return AmqpHeaderPhaseOutcome.Fail(AmqpErrorCode.AuthenticationFailed, message);
    }

    private static List<AmqpHeaderPhaseCase> CreateCases()
    {
        const AmqpHeaderPhaseSasl none = AmqpHeaderPhaseSasl.None;
        const AmqpHeaderPhaseSasl anonymous = AmqpHeaderPhaseSasl.Anonymous;
        const AmqpHeaderPhaseSasl challenge = AmqpHeaderPhaseSasl.Challenge;

        AmqpHeaderPhaseOutcome opened = AmqpHeaderPhaseOutcome.Open();
        string tooShort50 = Format(ErrorMessages.PeerIdleTimeoutTooShort, 50, AmqpConnectionSettings.MinRemoteIdleTimeout);
        string tooShort99 = Format(ErrorMessages.PeerIdleTimeoutTooShort, 99, AmqpConnectionSettings.MinRemoteIdleTimeout);
        string beginFirst = Format(ErrorMessages.OpenExpected, "begin");
        string saslFrameType = Format(ErrorMessages.FrameTypeUnexpected, AmqpFrame.SaslType);

        return
        [
            // H1: the success paths.
            Listener("a client without SASL opens with the AMQP header", "H1", none,
                [.. ClientOpens(), ExpectAlive()],
                AmqpHeaderPhaseOutcome.Open(connection =>
                {
                    connection.RemoteContainerId.ShouldBe("scripted-peer");
                    connection.Features.Get<IAmqpSaslConnectionInfo>().ShouldBeNull();
                })),
            Factory("the factory opens with the AMQP header to a server without SASL", "H1", none,
                [.. ServerOpens(), ExpectAlive()],
                AmqpHeaderPhaseOutcome.Open(connection =>
                {
                    connection.RemoteContainerId.ShouldBe("scripted-peer");
                    connection.Features.Get<IAmqpSaslConnectionInfo>().ShouldBeNull();
                })),
            Listener("a client authenticates with ANONYMOUS, then opens with the AMQP header", "H1", anonymous,
                [.. ClientAuthenticates(), .. ClientOpens(), ExpectAlive()],
                AmqpHeaderPhaseOutcome.Open(AuthenticatedWith("ANONYMOUS"))),
            Factory("the factory authenticates with ANONYMOUS, then opens with the AMQP header", "H1", anonymous,
                [.. ServerAuthenticates(), .. ServerOpens(), ExpectAlive()],
                AmqpHeaderPhaseOutcome.Open(AuthenticatedWith("ANONYMOUS"))),
            Listener("a client answers a challenge, then opens with the AMQP header", "H1", challenge,
                [
                    .. ClientStartsSasl(),
                    Write(Frame(SaslInit("X-CHALLENGE"), type: sasl)),
                    ExpectFrame("sasl-challenge", performative => Encoding.ASCII.GetString(performative.ShouldBeOfType<AmqpSaslChallengePerformative>().Challenge.Span).ShouldBe(TestChallengeSaslServerMechanism.Challenge)),
                    Write(Frame(Response(TestChallengeSaslServerMechanism.Answer), type: sasl)),
                    ExpectFrame("sasl-outcome", OutcomeIs(AmqpSaslCode.Ok)),
                    .. ClientOpens()
                ],
                AmqpHeaderPhaseOutcome.Open(connection => connection.Features.Get<IAmqpSaslConnectionInfo>().ShouldNotBeNull().Identity.ShouldBe(TestChallengeSaslServerMechanism.Identity))),
            Factory("the factory answers a challenge, then opens with the AMQP header", "H1", challenge,
                [
                    ExpectHeader(SaslHeader),
                    Write(SaslHeader, Frame(Mechanisms("X-CHALLENGE"), type: sasl)),
                    ExpectFrame("sasl-init"),
                    Write(Frame(Challenge("nonce"), type: sasl)),
                    ExpectFrame("sasl-response", performative => Encoding.ASCII.GetString(performative.ShouldBeOfType<AmqpSaslResponsePerformative>().Response.Span).ShouldBe("nonce-answer")),
                    Write(Frame(Outcome(AmqpSaslCode.Ok, "welcome"), type: sasl)),
                    .. ServerOpens()
                ],
                AmqpHeaderPhaseOutcome.Open(connection => Encoding.ASCII.GetString(connection.Features.Get<IAmqpSaslConnectionInfo>().ShouldNotBeNull().AdditionalData.Span).ShouldBe("welcome"))),

            // H2: an unsupported version is answered with the supported one.
            Listener("a client that asks for AMQP 1.0.1 is answered with 1.0.0 and refused", "H2", none,
                [Write(Header(0, 1, 0, 1)), ExpectHeader(AmqpHeader), ExpectEnd()],
                Refused(Unsupported(0, 1, 0, 1))),
            Listener("a client that asks for AMQP 2.0.0 is answered with 1.0.0 and refused", "H2", none,
                [Write(Header(0, 2, 0, 0)), ExpectHeader(AmqpHeader), ExpectEnd()],
                Refused(Unsupported(0, 2, 0, 0))),
            Listener("a client that asks for AMQP 0-9-1 is answered with 1.0.0 and refused", "H2", none,
                [Write(Header(0, 0, 9, 1)), ExpectHeader(AmqpHeader), ExpectEnd()],
                Refused(Unsupported(0, 0, 9, 1))),
            Listener("a client that asks for SASL 1.1.0 where SASL is required is answered with 1.0.0 and refused", "H2", anonymous,
                [Write(Header(3, 1, 1, 0)), ExpectHeader(SaslHeader), ExpectEnd()],
                Refused(Unsupported(3, 1, 1, 0))),

            // H3: a protocol id the server does not accept is answered with the one it requires.
            Listener("a client that asks for TLS is answered with the AMQP header and refused", "H3", none,
                [Write(Header(2)), ExpectHeader(AmqpHeader), ExpectEnd()],
                Refused(Unsupported(2, 1, 0, 0))),
            Listener("a client that asks for TLS where SASL is required is answered with the SASL header and refused", "H3", anonymous,
                [Write(Header(2)), ExpectHeader(SaslHeader), ExpectEnd()],
                Refused(Unsupported(2, 1, 0, 0))),
            Listener("a client that asks for SASL where none is offered is answered with the AMQP header and refused", "H3", none,
                [Write(SaslHeader), ExpectHeader(AmqpHeader), ExpectEnd()],
                Refused(Unsupported(3, 1, 0, 0))),
            Listener("a client that skips SASL where it is required is answered with the SASL header and refused", "H3", anonymous,
                [Write(AmqpHeader, Frame(Open())), ExpectHeader(SaslHeader), ExpectEnd()],
                Unauthenticated(ErrorMessages.SaslRequired)),
            Listener("a client that asks for an unassigned protocol id is answered with the AMQP header and refused", "H3", none,
                [Write(Header(5)), ExpectHeader(AmqpHeader), ExpectEnd()],
                Refused(Unsupported(5, 1, 0, 0))),

            // H4: bytes that are not a protocol header.
            Listener("a client that sends an HTTP request is answered with the AMQP header and refused", "H4", none,
                [Write(Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\n")), ExpectHeader(AmqpHeader), ExpectEnd()],
                Refused(ErrorMessages.ProtocolHeaderInvalid)),
            Listener("a client that sends an HTTP request where SASL is required is answered with the SASL header and refused", "H4", anonymous,
                [Write(Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\n")), ExpectHeader(SaslHeader), ExpectEnd()],
                Refused(ErrorMessages.ProtocolHeaderInvalid)),
            Listener("a client whose header misspells AMQP is answered with the AMQP header and refused", "H4", none,
                [Write("AMQX"u8.ToArray(), [0, 1, 0, 0]), ExpectHeader(AmqpHeader), ExpectEnd()],
                Refused(ErrorMessages.ProtocolHeaderInvalid)),

            // H5: the factory fails on any other answer than its own header.
            Factory("the factory fails when the server answers with AMQP 1.0.1", "H5", none,
                [ExpectHeader(AmqpHeader), ExpectFrame("open"), Write(Header(0, 1, 0, 1)), ExpectEnd()],
                Refused(Unsupported(0, 1, 0, 1))),
            Factory("the factory fails when the server answers with bytes that are not a header", "H5", none,
                [ExpectHeader(AmqpHeader), ExpectFrame("open"), Write(Encoding.ASCII.GetBytes("HTTP/1.1 400\r\n")), ExpectEnd()],
                Refused(ErrorMessages.ProtocolHeaderInvalid)),
            Factory("the factory fails when the server answers with the TLS header", "H5", none,
                [ExpectHeader(AmqpHeader), ExpectFrame("open"), Write(Header(2)), ExpectEnd()],
                Refused(Unsupported(2, 1, 0, 0))),
            Factory("the factory without SASL fails when the server requires it", "H5", none,
                [ExpectHeader(AmqpHeader), ExpectFrame("open"), Write(SaslHeader), ExpectEnd()],
                Unauthenticated(ErrorMessages.SaslAuthenticationRequired)),
            Factory("the factory that authenticates fails when the server offers no SASL", "H5", anonymous,
                [ExpectHeader(SaslHeader), Write(AmqpHeader), ExpectEnd()],
                Unauthenticated(ErrorMessages.SaslNotOffered)),
            Factory("the factory that authenticates fails when the server answers with the TLS header", "H5", anonymous,
                [ExpectHeader(SaslHeader), Write(Header(2)), ExpectEnd()],
                Refused(Unsupported(2, 1, 0, 0))),

            // H6: the SASL exchange keeps its order.
            Listener("a client that sends open instead of sasl-init is refused", "H6", anonymous,
                [.. ClientStartsSasl(), Write(Frame(Open(), type: sasl)), ExpectEnd()],
                Refused(Format(ErrorMessages.SaslFrameUnexpected, "open", "sasl-init"))),
            Listener("a client that sends sasl-mechanisms is refused", "H6", anonymous,
                [.. ClientStartsSasl(), Write(Frame(Mechanisms("ANONYMOUS"), type: sasl)), ExpectEnd()],
                Refused(Format(ErrorMessages.SaslFrameUnexpected, "sasl-mechanisms", "sasl-init"))),
            Listener("a client that answers a challenge with sasl-init is refused", "H6", challenge,
                [
                    .. ClientStartsSasl(),
                    Write(Frame(SaslInit("X-CHALLENGE"), type: sasl)),
                    ExpectFrame("sasl-challenge"),
                    Write(Frame(SaslInit("X-CHALLENGE"), type: sasl)),
                    ExpectEnd()
                ],
                Refused(Format(ErrorMessages.SaslFrameUnexpected, "sasl-init", "sasl-response"))),
            Factory("the factory refuses a sasl-outcome before sasl-mechanisms", "H6", anonymous,
                [ExpectHeader(SaslHeader), Write(SaslHeader, Frame(Outcome(AmqpSaslCode.Ok), type: sasl)), ExpectEnd()],
                Refused(Format(ErrorMessages.SaslFrameUnexpected, "sasl-outcome", "sasl-mechanisms"))),
            Factory("the factory refuses a server that answers sasl-init with sasl-init", "H6", anonymous,
                [
                    ExpectHeader(SaslHeader),
                    Write(SaslHeader, Frame(Mechanisms("ANONYMOUS"), type: sasl)),
                    ExpectFrame("sasl-init"),
                    Write(Frame(SaslInit("ANONYMOUS"), type: sasl)),
                    ExpectEnd()
                ],
                Refused(Format(ErrorMessages.SaslFrameUnexpected, "sasl-init", "sasl-outcome"))),
            Factory("the factory refuses sasl-mechanisms sent twice", "H6", anonymous,
                [
                    ExpectHeader(SaslHeader),
                    Write(SaslHeader, Frame(Mechanisms("ANONYMOUS"), type: sasl)),
                    ExpectFrame("sasl-init"),
                    Write(Frame(Mechanisms("ANONYMOUS"), type: sasl)),
                    ExpectEnd()
                ],
                Refused(Format(ErrorMessages.SaslFrameUnexpected, "sasl-mechanisms", "sasl-outcome"))),
            Factory("the factory refuses open in place of sasl-outcome", "H6", anonymous,
                [
                    ExpectHeader(SaslHeader),
                    Write(SaslHeader, Frame(Mechanisms("ANONYMOUS"), type: sasl)),
                    ExpectFrame("sasl-init"),
                    Write(Frame(Open(), type: sasl)),
                    ExpectEnd()
                ],
                Refused(Format(ErrorMessages.SaslFrameUnexpected, "open", "sasl-outcome"))),

            // H7: only SASL frames during the SASL exchange.
            Listener("a client that sends sasl-init in an AMQP frame is refused", "H7", anonymous,
                [.. ClientStartsSasl(), Write(Frame(SaslInit("ANONYMOUS"))), ExpectEnd()],
                Refused(Format(ErrorMessages.SaslFrameTypeInvalid, AmqpFrame.AmqpType))),
            Factory("the factory refuses sasl-mechanisms in an AMQP frame", "H7", anonymous,
                [ExpectHeader(SaslHeader), Write(SaslHeader, Frame(Mechanisms("ANONYMOUS"))), ExpectEnd()],
                Refused(Format(ErrorMessages.SaslFrameTypeInvalid, AmqpFrame.AmqpType))),

            // H8: one SASL performative per SASL frame, never none.
            Listener("a client that sends an empty SASL frame is refused", "H8", anonymous,
                [.. ClientStartsSasl(), Write(Frame(null, type: sasl)), ExpectEnd()],
                Refused(ErrorMessages.SaslFrameEmpty)),
            Factory("the factory refuses an empty SASL frame", "H8", anonymous,
                [ExpectHeader(SaslHeader), Write(SaslHeader, Frame(null, type: sasl)), ExpectEnd()],
                Refused(ErrorMessages.SaslFrameEmpty)),
            Listener("a client whose sasl-init frame holds a second value is refused", "H8", anonymous,
                [.. ClientStartsSasl(), Write(FrameWithBody([.. Encode(SaslInit("ANONYMOUS")), 0x40], type: sasl)), ExpectEnd()],
                Refused(ErrorMessages.SaslFrameBodyInvalid)),
            Factory("the factory refuses a sasl-mechanisms frame that holds a second value", "H8", anonymous,
                [ExpectHeader(SaslHeader), Write(SaslHeader, FrameWithBody([.. Encode(Mechanisms("ANONYMOUS")), 0x40], type: sasl)), ExpectEnd()],
                Refused(ErrorMessages.SaslFrameBodyInvalid)),

            // H9: 512 bytes at most; channel bytes and extended header ignored.
            Listener("a client's SASL frame with channel bytes and an extended header is taken", "H9", anonymous,
                [
                    .. ClientStartsSasl(),
                    Write(Frame(SaslInit("ANONYMOUS"), channel: 0x0102, type: sasl, extendedWords: 1)),
                    ExpectFrame("sasl-outcome", OutcomeIs(AmqpSaslCode.Ok)),
                    .. ClientOpens()
                ],
                AmqpHeaderPhaseOutcome.Open(AuthenticatedWith("ANONYMOUS"))),
            Factory("the factory takes a SASL frame with channel bytes and an extended header", "H9", anonymous,
                [
                    ExpectHeader(SaslHeader),
                    Write(SaslHeader, Frame(Mechanisms("ANONYMOUS"), channel: 7, type: sasl, extendedWords: 2)),
                    ExpectFrame("sasl-init"),
                    Write(Frame(Outcome(AmqpSaslCode.Ok), channel: 7, type: sasl, extendedWords: 1)),
                    .. ServerOpens()
                ],
                AmqpHeaderPhaseOutcome.Open(AuthenticatedWith("ANONYMOUS"))),
            Listener("a client's SASL frame that declares 513 bytes is refused from its header", "H9", anonymous,
                [.. ClientStartsSasl(), Write(FrameHeader(513, 2, sasl)), ExpectEnd()],
                Refused(Format(ErrorMessages.PeerSaslFrameTooLarge, 513, AmqpFrame.MinMaxFrameSize))),
            Factory("the factory refuses a SASL frame that declares 1024 bytes from its header", "H9", anonymous,
                [ExpectHeader(SaslHeader), Write(SaslHeader, FrameHeader(1024, 2, sasl)), ExpectEnd()],
                Refused(Format(ErrorMessages.PeerSaslFrameTooLarge, 1024, AmqpFrame.MinMaxFrameSize))),

            // H10: the AMQP header follows a successful outcome.
            Listener("a client that sends the SASL header again after its outcome is answered with the AMQP header and refused", "H10", anonymous,
                [.. ClientAuthenticates(), Write(SaslHeader), ExpectHeader(AmqpHeader), ExpectEnd()],
                Refused(Unsupported(3, 1, 0, 0))),
            Listener("a client that sends another SASL frame after its outcome is answered with the AMQP header and refused", "H10", anonymous,
                [.. ClientAuthenticates(), Write(Frame(Response("again"), type: sasl)), ExpectHeader(AmqpHeader), ExpectEnd()],
                Refused(ErrorMessages.ProtocolHeaderInvalid)),
            Factory("the factory fails when the server answers with the SASL header again after its outcome", "H10", anonymous,
                [.. ServerAuthenticates(), ExpectHeader(AmqpHeader), ExpectFrame("open"), Write(SaslHeader), ExpectEnd()],
                Refused(Unsupported(3, 1, 0, 0))),

            // H11: failed outcomes.
            Factory("the factory fails on the outcome auth", "H11", anonymous,
                [
                    ExpectHeader(SaslHeader),
                    Write(SaslHeader, Frame(Mechanisms("ANONYMOUS"), type: sasl)),
                    ExpectFrame("sasl-init"),
                    Write(Frame(Outcome(AmqpSaslCode.Auth), type: sasl)),
                    ExpectEnd()
                ],
                Unauthenticated(Format(ErrorMessages.SaslAuthenticationFailed, AmqpSaslCode.Auth))),
            Factory("the factory fails on the outcome sys-perm", "H11", anonymous,
                [
                    ExpectHeader(SaslHeader),
                    Write(SaslHeader, Frame(Mechanisms("ANONYMOUS"), type: sasl)),
                    ExpectFrame("sasl-init"),
                    Write(Frame(Outcome(AmqpSaslCode.SysPerm), type: sasl)),
                    ExpectEnd()
                ],
                Unauthenticated(Format(ErrorMessages.SaslAuthenticationFailed, AmqpSaslCode.SysPerm))),
            Listener("a client that chooses a mechanism not offered is sent the outcome auth and refused", "H11", anonymous,
                [.. ClientStartsSasl(), Write(Frame(SaslInit("X-UNKNOWN"), type: sasl)), ExpectFrame("sasl-outcome", OutcomeIs(AmqpSaslCode.Auth)), ExpectEnd()],
                Unauthenticated(Format(ErrorMessages.SaslMechanismUnknown, "X-UNKNOWN"))),
            Factory("the factory fails when the server does not offer its mechanism", "H11", anonymous,
                [ExpectHeader(SaslHeader), Write(SaslHeader, Frame(Mechanisms("PLAIN"), type: sasl)), ExpectEnd()],
                Unauthenticated(Format(ErrorMessages.SaslMechanismNotOffered, "ANONYMOUS"))),

            // H12: open comes first.
            Listener("a client that sends begin before open is closed with illegal-state", "H12", none,
                [Write(AmqpHeader, Frame(AmqpTestPeer.CreateBegin())), ExpectHeader(AmqpHeader), ExpectFrame("open"), ExpectClose(AmqpErrorCondition.IllegalState, beginFirst), ExpectEnd()],
                Closed(AmqpErrorCondition.IllegalState, beginFirst)),
            Factory("the factory closes a server that sends begin before open with illegal-state", "H12", none,
                [ExpectHeader(AmqpHeader), ExpectFrame("open"), Write(AmqpHeader, Frame(AmqpTestPeer.CreateBegin())), ExpectClose(AmqpErrorCondition.IllegalState, beginFirst), ExpectEnd()],
                Closed(AmqpErrorCondition.IllegalState, beginFirst)),
            Listener("a client that sends a SASL frame after the AMQP header is closed with a framing error", "H12", none,
                [Write(AmqpHeader, Frame(SaslInit("ANONYMOUS"), type: sasl)), ExpectHeader(AmqpHeader), ExpectFrame("open"), ExpectClose(AmqpErrorCondition.FramingError, saslFrameType), ExpectEnd()],
                Closed(AmqpErrorCondition.FramingError, saslFrameType)),
            Factory("the factory closes a server that sends an empty SASL frame after the AMQP header with a framing error", "H12", none,
                [ExpectHeader(AmqpHeader), ExpectFrame("open"), Write(AmqpHeader, Frame(null, type: sasl)), ExpectClose(AmqpErrorCondition.FramingError, saslFrameType), ExpectEnd()],
                Closed(AmqpErrorCondition.FramingError, saslFrameType)),
            Listener("a client whose first frame declares a data offset of 1 is closed with a framing error", "H12", none,
                [Write(AmqpHeader, FrameHeader(8, 1)), ExpectHeader(AmqpHeader), ExpectFrame("open"), ExpectClose(AmqpErrorCondition.FramingError, ErrorMessages.FrameDataOffsetInvalid), ExpectEnd()],
                Closed(AmqpErrorCondition.FramingError, ErrorMessages.FrameDataOffsetInvalid)),

            // H13: open on channel 0.
            Listener("a client that sends open on channel 1 is closed with a framing error", "H13", none,
                [Write(AmqpHeader, Frame(Open(), channel: 1)), ExpectHeader(AmqpHeader), ExpectFrame("open"), ExpectClose(AmqpErrorCondition.FramingError, Format(ErrorMessages.OpenChannelInvalid, 1)), ExpectEnd()],
                Closed(AmqpErrorCondition.FramingError, Format(ErrorMessages.OpenChannelInvalid, 1))),
            Factory("the factory closes a server that sends open on channel 3 with a framing error", "H13", none,
                [ExpectHeader(AmqpHeader), ExpectFrame("open"), Write(AmqpHeader, Frame(Open(), channel: 3)), ExpectClose(AmqpErrorCondition.FramingError, Format(ErrorMessages.OpenChannelInvalid, 3)), ExpectEnd()],
                Closed(AmqpErrorCondition.FramingError, Format(ErrorMessages.OpenChannelInvalid, 3))),

            // H14: empty frames before open.
            Listener("a client's empty frames on channel 0 before its open are skipped", "H14", none,
                [Write(AmqpHeader, Frame(null), Frame(null), Frame(Open())), ExpectHeader(AmqpHeader), ExpectFrame("open"), ExpectAlive()],
                opened),
            Factory("a server's empty frame on channel 0 before its open is skipped", "H14", none,
                [ExpectHeader(AmqpHeader), ExpectFrame("open"), Write(AmqpHeader, Frame(null), Frame(Open())), ExpectAlive()],
                opened),
            Listener("a client's empty frame on channel 2 before its open is closed with a framing error", "H14", none,
                [Write(AmqpHeader, Frame(null, channel: 2), Frame(Open())), ExpectHeader(AmqpHeader), ExpectFrame("open"), ExpectClose(AmqpErrorCondition.FramingError, Format(ErrorMessages.ChannelOutOfRange, 2, 0)), ExpectEnd()],
                Closed(AmqpErrorCondition.FramingError, Format(ErrorMessages.ChannelOutOfRange, 2, 0))),
            Factory("a server's empty SASL frame before its open is closed with a framing error", "H14", none,
                [ExpectHeader(AmqpHeader), ExpectFrame("open"), Write(AmqpHeader, Frame(null, type: sasl), Frame(Open())), ExpectClose(AmqpErrorCondition.FramingError, saslFrameType), ExpectEnd()],
                Closed(AmqpErrorCondition.FramingError, saslFrameType)),

            // H15: pipelining.
            Listener("a client that pipelines its header, open and begin is answered in order", "H15", none,
                [
                    Write(AmqpHeader, Frame(Open()), Frame(AmqpTestPeer.CreateBegin())),
                    ExpectHeader(AmqpHeader),
                    ExpectFrame("open"),
                    ExpectFrame("begin", performative => performative.ShouldBeOfType<AmqpBeginPerformative>().RemoteChannel.ShouldBe((ushort)0))
                ],
                opened),
            Factory("a server that pipelines its header, open and begin is answered in order", "H15", none,
                [
                    ExpectHeader(AmqpHeader),
                    ExpectFrame("open"),
                    Write(AmqpHeader, Frame(Open()), Frame(AmqpTestPeer.CreateBegin())),
                    ExpectFrame("begin", performative => performative.ShouldBeOfType<AmqpBeginPerformative>().RemoteChannel.ShouldBe((ushort)0))
                ],
                opened),
            Listener("a client that pipelines its AMQP header and open behind its last SASL frame is answered in order", "H15", anonymous,
                [
                    .. ClientStartsSasl(),
                    Write(Frame(SaslInit("ANONYMOUS"), type: sasl), AmqpHeader, Frame(Open())),
                    ExpectFrame("sasl-outcome", OutcomeIs(AmqpSaslCode.Ok)),
                    ExpectHeader(AmqpHeader),
                    ExpectFrame("open"),
                    ExpectAlive()
                ],
                AmqpHeaderPhaseOutcome.Open(AuthenticatedWith("ANONYMOUS"))),

            // H16: idle timeouts declared and kept.
            Listener("the listener declares half its idle timeout in its open", "H16", none,
                [Write(AmqpHeader, Frame(Open())), ExpectHeader(AmqpHeader), ExpectFrame("open", performative => performative.ShouldBeOfType<AmqpOpenPerformative>().IdleTimeout.ShouldBe(30_000u))],
                opened),
            Factory("the factory declares half its idle timeout in its open", "H16", none,
                [ExpectHeader(AmqpHeader), ExpectFrame("open", performative => performative.ShouldBeOfType<AmqpOpenPerformative>().IdleTimeout.ShouldBe(30_000u)), Write(AmqpHeader, Frame(Open()))],
                opened),
            Listener("the listener keeps a client's idle timeout of 200 ms with empty frames on channel 0", "H16", none,
                [.. ClientOpens(Open(200)), ExpectEmptyFrame(_keepAliveWindow), ExpectEmptyFrame(_keepAliveWindow)],
                AmqpHeaderPhaseOutcome.Open(KeepsIdleTimeout(200))),
            Factory("the factory keeps a server's idle timeout of 200 ms with empty frames on channel 0", "H16", none,
                [.. ServerOpens(Open(200)), ExpectEmptyFrame(_keepAliveWindow), ExpectEmptyFrame(_keepAliveWindow)],
                AmqpHeaderPhaseOutcome.Open(KeepsIdleTimeout(200))),
            Listener("the listener sends no empty frames to a client that declares an idle timeout of 0", "H16", none,
                [.. ClientOpens(Open(0)), ExpectSilence(_silence), ExpectAlive()],
                AmqpHeaderPhaseOutcome.Open(KeepsIdleTimeout(0))),
            Factory("the factory sends no empty frames to a server that declares no idle timeout", "H16", none,
                [.. ServerOpens(), ExpectSilence(_silence), ExpectAlive()],
                AmqpHeaderPhaseOutcome.Open(KeepsIdleTimeout(0))),
            Listener("the listener sends no empty frame after its close", "H16", none,
                [
                    .. ClientOpens(Open(200)),
                    Write(Frame(AmqpTestPeer.CreateBegin(), channel: 300)),
                    ExpectClose(AmqpErrorCondition.FramingError, Format(ErrorMessages.ChannelOutOfRange, 300, 255)),
                    ExpectSilence(_silence)
                ],
                opened),

            // H17: an idle timeout too short to keep is refused with the reason.
            Listener("a client that declares an idle timeout of 50 ms is closed with not-implemented", "H17", none,
                [Write(AmqpHeader, Frame(Open(50))), ExpectHeader(AmqpHeader), ExpectFrame("open"), ExpectClose(AmqpErrorCondition.NotImplemented, tooShort50), ExpectEnd()],
                AmqpHeaderPhaseOutcome.Fail(AmqpErrorCode.Other, tooShort50, AmqpErrorCondition.NotImplemented)),
            Factory("the factory closes a server that declares an idle timeout of 99 ms with not-implemented", "H17", none,
                [ExpectHeader(AmqpHeader), ExpectFrame("open"), Write(AmqpHeader, Frame(Open(99))), ExpectClose(AmqpErrorCondition.NotImplemented, tooShort99), ExpectEnd()],
                AmqpHeaderPhaseOutcome.Fail(AmqpErrorCode.Other, tooShort99, AmqpErrorCondition.NotImplemented)),
            Listener("the listener keeps a client's idle timeout of exactly 100 ms", "H17", none,
                [.. ClientOpens(Open(100)), ExpectEmptyFrame(_keepAliveWindow), ExpectEmptyFrame(_keepAliveWindow)],
                AmqpHeaderPhaseOutcome.Open(KeepsIdleTimeout(100))),

            // H18: channels and frame types after open.
            Listener("a client's empty frames on channels 0 and 7 keep the connection open", "H18", none,
                [.. ClientOpens(), Write(Frame(null), Frame(null, channel: 7)), ExpectAlive()],
                opened),
            Factory("a server's empty frame on channel 255, the factory's channel-max, keeps the connection open", "H18", none,
                [.. ServerOpens(), Write(Frame(null, channel: 255)), ExpectAlive()],
                opened),
            Listener("a client's empty frame on channel 256, above channel-max, is closed with a framing error", "H18", none,
                [.. ClientOpens(), Write(Frame(null, channel: 256)), ExpectClose(AmqpErrorCondition.FramingError, Format(ErrorMessages.ChannelOutOfRange, 256, 255))],
                opened),
            Factory("a server's close on channel 300, above channel-max, is closed with a framing error", "H18", none,
                [.. ServerOpens(), Write(Frame(new AmqpClosePerformative(), channel: 300)), ExpectClose(AmqpErrorCondition.FramingError, Format(ErrorMessages.ChannelOutOfRange, 300, 255))],
                opened),
            Listener("a client's close on channel 300 that answers the listener's close ends the connection", "H18", none,
                [
                    .. ClientOpens(),
                    Write(Frame(AmqpTestPeer.CreateBegin(), channel: 300)),
                    ExpectClose(AmqpErrorCondition.FramingError, Format(ErrorMessages.ChannelOutOfRange, 300, 255)),
                    Write(Frame(new AmqpClosePerformative(), channel: 300)),
                    ExpectEnd()
                ],
                opened),
            Listener("a client's empty SASL frame after open is closed with a framing error", "H18", none,
                [.. ClientOpens(), Write(Frame(null, type: sasl)), ExpectClose(AmqpErrorCondition.FramingError, saslFrameType)],
                opened),
            Factory("a server's empty frame of type 0x41 after open is closed with a framing error", "H18", none,
                [.. ServerOpens(), Write(Frame(null, type: 0x41)), ExpectClose(AmqpErrorCondition.FramingError, Format(ErrorMessages.FrameTypeUnexpected, 0x41))],
                opened),

            // H19: the frame layout.
            Listener("a client's open with an extended header is taken", "H19", none,
                [Write(AmqpHeader, Frame(Open(), extendedWords: 1)), ExpectHeader(AmqpHeader), ExpectFrame("open"), ExpectAlive()],
                opened),
            Factory("a server's begin with an extended header is taken", "H19", none,
                [
                    .. ServerOpens(),
                    Write(Frame(AmqpTestPeer.CreateBegin(), extendedWords: 2)),
                    ExpectFrame("begin", performative => performative.ShouldBeOfType<AmqpBeginPerformative>().RemoteChannel.ShouldBe((ushort)0))
                ],
                opened),
            Listener("a client's frame with a data offset of 1 after open is closed with a framing error", "H19", none,
                [.. ClientOpens(), Write(FrameHeader(8, 1)), ExpectClose(AmqpErrorCondition.FramingError, ErrorMessages.FrameDataOffsetInvalid)],
                opened),
            Factory("a server's frame that declares 7 bytes after open is closed with a framing error", "H19", none,
                [.. ServerOpens(), Write(FrameHeader(7, 2)), ExpectClose(AmqpErrorCondition.FramingError, ErrorMessages.FrameSizeInvalid)],
                opened)
        ];
    }
}
