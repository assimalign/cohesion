using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Amqp.Connections.Internal;

/// <summary>
/// An AMQP connection over a carrier duplex connection: the carrier's pipe carries the AMQP protocol
/// headers and frames, and the carrier's identity, endpoints, capabilities, features and lifetime are
/// the connection's own.
/// </summary>
/// <remarks>
/// Over a single-stream carrier the carrier is the connection the listener accepted or the factory
/// connected. Over a multiplexed carrier it is an <see cref="AmqpMultiplexedCarrier"/> whose AMQP stream
/// has been acquired. Disposing or aborting the AMQP connection disposes or aborts the carrier.
/// </remarks>
[global::System.Diagnostics.DebuggerDisplay("AmqpCarrierConnection {Id,nq}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed class AmqpCarrierConnection : AmqpConnection
{
    private readonly IDuplexConnection _carrier;
    private readonly uint _maxFrameSize;
    private readonly SemaphoreSlim _protocolLock;
    private readonly SemaphoreSlim _writeLock;
    private ProtocolPhase _phase;

    public AmqpCarrierConnection(IDuplexConnection carrier, AmqpConnectionSettings settings)
    {
        _carrier = carrier;
        _maxFrameSize = settings.MaxFrameSize;
        _protocolLock = new SemaphoreSlim(1, 1);
        _writeLock = new SemaphoreSlim(1, 1);
        _phase = new ProtocolPhase(settings.InitialProtocolHeader, remote: null);
    }

    public override ConnectionId Id => _carrier.Id;

    public override EndPoint? LocalEndPoint => _carrier.LocalEndPoint;

    public override EndPoint? RemoteEndPoint => _carrier.RemoteEndPoint;

    public override ConnectionCapabilities Capabilities => _carrier.Capabilities;

    public override ConnectionState State => _carrier.State;

    public override CancellationToken ConnectionClosed => _carrier.ConnectionClosed;

    public override ConnectionDirection Direction => _carrier.Direction;

    public override PipeReader Input => _carrier.Input;

    public override PipeWriter Output => _carrier.Output;

    public override AmqpProtocolHeader LocalProtocolHeader => Volatile.Read(ref _phase).Local;

    public override AmqpProtocolHeader? RemoteProtocolHeader => Volatile.Read(ref _phase).Remote;

    protected override IConnectionFeatureCollection? InnerFeatures => _carrier.Features;

    public override void Abort(Exception? reason = null)
    {
        _carrier.Abort(reason);
    }

    public override ValueTask DisposeAsync()
    {
        return _carrier.DisposeAsync();
    }

    public override async ValueTask<AmqpProtocolHeader> NegotiateAsync(CancellationToken cancellationToken = default)
    {
        AmqpProtocolHeader? negotiated = Volatile.Read(ref _phase).Remote;

        if (negotiated.HasValue)
        {
            return negotiated.Value;
        }

        await _protocolLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            ProtocolPhase phase = _phase;

            if (phase.Remote.HasValue)
            {
                return phase.Remote.Value;
            }

            await WriteProtocolHeaderAsync(phase.Local, cancellationToken).ConfigureAwait(false);
            AmqpProtocolHeader remote = await ReadProtocolHeaderAsync(cancellationToken).ConfigureAwait(false);

            Volatile.Write(ref _phase, new ProtocolPhase(phase.Local, remote));

            return remote;
        }
        finally
        {
            _protocolLock.Release();
        }
    }

    public override async ValueTask<AmqpProtocolHeader> SwitchProtocolAsync(
        AmqpProtocolHeader protocolHeader,
        CancellationToken cancellationToken = default)
    {
        await _protocolLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            // The previous phase ends here: until the peer's header for the next phase arrives, frames
            // are refused rather than encoded for a phase neither side is in.
            Volatile.Write(ref _phase, new ProtocolPhase(protocolHeader, remote: null));

            await WriteProtocolHeaderAsync(protocolHeader, cancellationToken).ConfigureAwait(false);
            AmqpProtocolHeader remote = await ReadProtocolHeaderAsync(cancellationToken).ConfigureAwait(false);

            Volatile.Write(ref _phase, new ProtocolPhase(protocolHeader, remote));

            return remote;
        }
        finally
        {
            _protocolLock.Release();
        }
    }

    public override async IAsyncEnumerable<AmqpFrame> ReceiveAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        AmqpFrameType frameType = GetNegotiatedPhase().Local.FrameType;
        PipeReader reader = Input;

        while (true)
        {
            ReadResult result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            ReadOnlySequence<byte> buffer = result.Buffer;

            try
            {
                while (AmqpEncoding.TryReadFrame(ref buffer, frameType, out AmqpFrame frame))
                {
                    yield return frame;
                }

                if (result.IsCompleted)
                {
                    if (buffer.Length > 0)
                    {
                        throw new AmqpProtocolException(ErrorMessages.FrameIncomplete);
                    }

                    yield break;
                }
            }
            finally
            {
                reader.AdvanceTo(buffer.Start, buffer.End);
            }
        }
    }

    public override async ValueTask SendAsync(AmqpFrame frame, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);

        AmqpFrameType frameType = GetNegotiatedPhase().Local.FrameType;
        byte[] payload = AmqpEncoding.EncodeFrame(frame, frameType, _maxFrameSize);

        await WriteAsync(payload, cancellationToken).ConfigureAwait(false);
    }

    private ProtocolPhase GetNegotiatedPhase()
    {
        ProtocolPhase phase = Volatile.Read(ref _phase);

        if (!phase.Remote.HasValue)
        {
            throw new InvalidOperationException(ErrorMessages.ProtocolHeaderNotNegotiated);
        }

        return phase;
    }

    private ValueTask WriteProtocolHeaderAsync(AmqpProtocolHeader header, CancellationToken cancellationToken)
    {
        return WriteAsync(AmqpEncoding.EncodeProtocolHeader(header), cancellationToken);
    }

    private async ValueTask WriteAsync(byte[] payload, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            // PipeWriter.WriteAsync flushes, so the bytes are readable by the peer when it completes.
            await Output.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async ValueTask<AmqpProtocolHeader> ReadProtocolHeaderAsync(CancellationToken cancellationToken)
    {
        PipeReader reader = Input;

        while (true)
        {
            ReadResult result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            ReadOnlySequence<byte> buffer = result.Buffer;

            if (buffer.Length >= 8)
            {
                byte[] headerBytes = buffer.Slice(0, 8).ToArray();
                // Mark examined == consumed (single-arg AdvanceTo): we consumed
                // exactly the 8 protocol-header bytes. If the peer coalesced the
                // header with the following frame bytes into a single write (TCP),
                // the next ReadAsync must surface that buffered remainder
                // immediately. Passing examined=buffer.End would instead make
                // ReadAsync wait for *new* bytes beyond what is already buffered,
                // deadlocking lock-step exchanges such as SASL (header +
                // sasl-mechanisms sent in one write).
                reader.AdvanceTo(buffer.GetPosition(8));
                return AmqpEncoding.DecodeProtocolHeader(headerBytes);
            }

            reader.AdvanceTo(buffer.Start, buffer.End);

            if (result.IsCompleted)
            {
                throw new AmqpProtocolException(ErrorMessages.ProtocolHeaderIncomplete);
            }
        }
    }

    /// <summary>
    /// The protocol headers of one phase. A phase is replaced as a whole, so a reader that does not take
    /// the protocol lock never observes a local header paired with another phase's remote header.
    /// </summary>
    private sealed class ProtocolPhase
    {
        public ProtocolPhase(AmqpProtocolHeader local, AmqpProtocolHeader? remote)
        {
            Local = local;
            Remote = remote;
        }

        public AmqpProtocolHeader Local { get; }

        public AmqpProtocolHeader? Remote { get; }
    }

    private sealed class DebugView
    {
        private readonly AmqpCarrierConnection _target;

        public DebugView(AmqpCarrierConnection value)
        {
            _target = value;
        }

        public ConnectionId Id => _target.Id;
        public EndPoint? LocalEndPoint => _target.LocalEndPoint;
        public EndPoint? RemoteEndPoint => _target.RemoteEndPoint;
        public ConnectionCapabilities Capabilities => _target.Capabilities;
        public ConnectionState State => _target.State;
        public ConnectionDirection Direction => _target.Direction;
        public AmqpProtocolHeader LocalProtocolHeader => _target.LocalProtocolHeader;
        public AmqpProtocolHeader? RemoteProtocolHeader => _target.RemoteProtocolHeader;
    }
}
