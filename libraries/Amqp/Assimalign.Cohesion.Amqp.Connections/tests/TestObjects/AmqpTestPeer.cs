using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Assimalign.Cohesion.Amqp;
using Assimalign.Cohesion.Amqp.Connections.Internal;

namespace Assimalign.Cohesion.Amqp.Connections.Tests;

/// <summary>
/// Plays a scripted AMQP peer on the far end of a <see cref="TestConnection"/>, so a test can send the
/// engine under test exactly the frames it wants, including ones that break the protocol, and read the
/// frames the engine answers with. Frames are written and read with the library's own codec.
/// </summary>
/// <remarks>
/// Disposing the peer hangs up: it completes the far end's output, as a peer that closes its socket does,
/// so the engine's own disposal does not wait for a <c>close</c> the scripted peer would never send.
/// </remarks>
internal sealed class AmqpTestPeer : IAsyncDisposable
{
    public static readonly EndPoint EndPoint = new DnsEndPoint("amqp.test", 5672);

    private readonly TestConnection _carrier;
    private readonly PipeReader _input;
    private readonly PipeWriter _output;

    public AmqpTestPeer(TestConnection carrier)
    {
        _carrier = carrier;
        _input = carrier.PeerInput;
        _output = carrier.PeerOutput;
    }

    /// <summary>
    /// Gets the carrier whose near end the engine under test owns.
    /// </summary>
    public TestConnection Carrier => _carrier;

    /// <summary>
    /// Gets or sets the largest frame the peer reads from the engine.
    /// </summary>
    public uint MaxFrameSize { get; set; } = 1024 * 1024;

    /// <summary>
    /// Starts a listener whose one carrier this peer plays, as the connecting client. The returned task
    /// completes once the listener has established the connection the peer opens.
    /// </summary>
    public static (AmqpTestPeer Peer, AmqpConnectionListener Listener, Task<IAmqpConnection> Accept) StartListener(
        AmqpConnectionListenerOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        TestConnection carrier = new();
        TestConnectionListener carrierListener = new();

        carrierListener.Enqueue(carrier);

        AmqpConnectionListener listener = AmqpConnectionListener.Create(carrierListener, options);

        return (new AmqpTestPeer(carrier), listener, listener.AcceptAsync(cancellationToken).AsTask());
    }

    /// <summary>
    /// Starts a factory whose one carrier this peer plays, as the accepting server. The returned task
    /// completes once the factory has established the connection it opens to the peer.
    /// </summary>
    public static (AmqpTestPeer Peer, AmqpConnectionFactory Factory, Task<IAmqpConnection> Connect) StartFactory(
        AmqpConnectionFactoryOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        TestConnection carrier = new();
        TestConnectionFactory carrierFactory = new();

        carrierFactory.Enqueue(carrier);

        AmqpConnectionFactory factory = AmqpConnectionFactory.Create(carrierFactory, options);

        return (new AmqpTestPeer(carrier), factory, factory.ConnectAsync(EndPoint, cancellationToken).AsTask());
    }

    /// <summary>
    /// Creates the <c>open</c> a scripted peer sends unless a test needs another.
    /// </summary>
    public static AmqpOpenPerformative CreateOpen(uint maxFrameSize = 65536, ushort channelMax = 15)
    {
        return new AmqpOpenPerformative
        {
            ContainerId = "scripted-peer",
            MaxFrameSize = maxFrameSize,
            ChannelMax = channelMax
        };
    }

    /// <summary>
    /// Creates the <c>begin</c> a scripted peer sends unless a test needs another.
    /// </summary>
    public static AmqpBeginPerformative CreateBegin(ushort? remoteChannel = null, uint incomingWindow = 100, uint handleMax = 7)
    {
        return new AmqpBeginPerformative
        {
            RemoteChannel = remoteChannel,
            NextOutgoingId = 1,
            IncomingWindow = incomingWindow,
            OutgoingWindow = 100,
            HandleMax = handleMax
        };
    }

    public async Task WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        await _output.WriteAsync(bytes, cancellationToken);
    }

    public Task WriteProtocolHeaderAsync(byte protocolId, CancellationToken cancellationToken)
    {
        return WriteAsync(new byte[] { (byte)'A', (byte)'M', (byte)'Q', (byte)'P', protocolId, 1, 0, 0 }, cancellationToken);
    }

    public async Task WriteFrameAsync(
        ushort channel,
        AmqpPerformative performative,
        CancellationToken cancellationToken,
        ReadOnlyMemory<byte> payload = default,
        byte type = AmqpFrame.AmqpType)
    {
        using AmqpBufferWriter buffer = new();

        AmqpFrame.Write(buffer, type, channel, performative, payload.Span);
        await WriteAsync(buffer.ToArray(), cancellationToken);
    }

    /// <summary>
    /// Writes an AMQP frame around a body encoded by hand, such as a performative the encoder would refuse.
    /// </summary>
    public async Task WriteFrameBodyAsync(ushort channel, ReadOnlyMemory<byte> body, CancellationToken cancellationToken)
    {
        byte[] frame = new byte[8 + body.Length];

        BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)frame.Length);
        frame[4] = 2;
        frame[5] = AmqpFrame.AmqpType;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(6), channel);
        body.CopyTo(frame.AsMemory(8));
        await WriteAsync(frame, cancellationToken);
    }

    public async Task<byte[]> ReadProtocolHeaderAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            ReadResult result = await _input.ReadAsync(cancellationToken);
            ReadOnlySequence<byte> buffer = result.Buffer;

            if (buffer.Length >= AmqpFrame.ProtocolHeaderSize)
            {
                byte[] header = buffer.Slice(0, AmqpFrame.ProtocolHeaderSize).ToArray();

                _input.AdvanceTo(buffer.GetPosition(AmqpFrame.ProtocolHeaderSize));
                return header;
            }

            if (result.IsCompleted)
            {
                _input.AdvanceTo(buffer.End);
                throw new EndOfStreamException("The engine closed the carrier before writing a protocol header.");
            }

            _input.AdvanceTo(buffer.Start, buffer.End);
        }
    }

    /// <summary>
    /// Hangs up: completes the far end's output, which ends the engine's input.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        _carrier.CompletePeerOutput();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Reads the next frame the engine wrote, or returns <see langword="null"/> once the engine closed the
    /// carrier's output.
    /// </summary>
    public async Task<AmqpTestFrame?> ReadFrameAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            ReadResult result = await _input.ReadAsync(cancellationToken);
            ReadOnlySequence<byte> buffer = result.Buffer;

            if (AmqpFrame.TryRead(ref buffer, MaxFrameSize, out AmqpFrameHeader header, out ReadOnlySequence<byte> body))
            {
                AmqpTestFrame frame = AmqpTestFrame.Decode(header, body.ToArray());

                _input.AdvanceTo(buffer.Start);
                return frame;
            }

            if (result.IsCompleted)
            {
                _input.AdvanceTo(buffer.End);
                return null;
            }

            _input.AdvanceTo(buffer.Start, buffer.End);
        }
    }

    /// <summary>
    /// Reads frames until one carries a performative, skipping empty frames, and returns it as
    /// <typeparamref name="T"/>.
    /// </summary>
    public async Task<T> ReadPerformativeAsync<T>(CancellationToken cancellationToken)
        where T : AmqpPerformative
    {
        AmqpTestFrame frame = await ReadNonEmptyFrameAsync(cancellationToken);

        return frame.Performative.ShouldBeOfType<T>();
    }

    /// <summary>
    /// Reads frames until one carries a performative, skipping empty frames.
    /// </summary>
    public async Task<AmqpTestFrame> ReadNonEmptyFrameAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            AmqpTestFrame? frame = await ReadFrameAsync(cancellationToken);

            frame.ShouldNotBeNull("The engine closed the carrier before writing the expected frame.");

            if (frame.Performative is not null)
            {
                return frame;
            }
        }
    }

    /// <summary>
    /// Reads every frame until the engine closes the carrier's output, skipping empty frames.
    /// </summary>
    public async Task<IReadOnlyList<AmqpPerformative>> ReadToEndAsync(CancellationToken cancellationToken)
    {
        List<AmqpPerformative> performatives = [];

        while (await ReadFrameAsync(cancellationToken) is AmqpTestFrame frame)
        {
            if (frame.Performative is not null)
            {
                performatives.Add(frame.Performative);
            }
        }

        return performatives;
    }

    /// <summary>
    /// Opens the connection as the client: writes the AMQP header and <paramref name="open"/>, then reads
    /// the engine's header and <c>open</c>.
    /// </summary>
    public async Task<AmqpOpenPerformative> OpenAsClientAsync(AmqpOpenPerformative? open, CancellationToken cancellationToken)
    {
        await WriteProtocolHeaderAsync(AmqpFrame.AmqpProtocolId, cancellationToken);
        await WriteFrameAsync(0, open ?? CreateOpen(), cancellationToken);

        (await ReadProtocolHeaderAsync(cancellationToken)).ShouldBe(AmqpFrame.AmqpProtocolHeader.ToArray());

        return await ReadPerformativeAsync<AmqpOpenPerformative>(cancellationToken);
    }

    /// <summary>
    /// Opens the connection as the server: reads the engine's header and <c>open</c>, then writes the AMQP
    /// header and <paramref name="open"/>.
    /// </summary>
    public async Task<AmqpOpenPerformative> OpenAsServerAsync(AmqpOpenPerformative? open, CancellationToken cancellationToken)
    {
        (await ReadProtocolHeaderAsync(cancellationToken)).ShouldBe(AmqpFrame.AmqpProtocolHeader.ToArray());

        AmqpOpenPerformative remote = await ReadPerformativeAsync<AmqpOpenPerformative>(cancellationToken);

        await WriteProtocolHeaderAsync(AmqpFrame.AmqpProtocolId, cancellationToken);
        await WriteFrameAsync(0, open ?? CreateOpen(), cancellationToken);
        return remote;
    }

    /// <summary>
    /// Begins a session on channel 0 and reads the engine's answering <c>begin</c>.
    /// </summary>
    public async Task<AmqpBeginPerformative> BeginAsync(AmqpBeginPerformative? begin, CancellationToken cancellationToken)
    {
        await WriteFrameAsync(0, begin ?? CreateBegin(), cancellationToken);

        AmqpTestFrame frame = await ReadNonEmptyFrameAsync(cancellationToken);
        AmqpBeginPerformative answer = frame.Performative.ShouldBeOfType<AmqpBeginPerformative>();

        answer.RemoteChannel.ShouldBe((ushort)0);
        return answer;
    }

    /// <summary>
    /// Reads frames until the engine closes the connection and returns the error its <c>close</c> carried.
    /// </summary>
    public async Task<AmqpError?> ReadCloseAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            AmqpTestFrame frame = await ReadNonEmptyFrameAsync(cancellationToken);

            if (frame.Performative is AmqpClosePerformative close)
            {
                return close.Error;
            }
        }
    }
}
