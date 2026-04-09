using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Transports;

namespace Assimalign.Cohesion.Amqp.Transports.Internal;

internal sealed class AmqpTransportConnectionContext : AmqpConnectionContext
{
    private readonly ITransportConnectionContext _context;
    private readonly AmqpTransportOptions _options;
    private readonly SemaphoreSlim _protocolLock;
    private readonly SemaphoreSlim _writeLock;

    private AmqpProtocolHeader _localProtocolHeader;
    private AmqpProtocolHeader? _remoteProtocolHeader;

    public AmqpTransportConnectionContext(ITransportConnectionContext context, AmqpTransportOptions options)
    {
        _context = context;
        _options = options;
        _localProtocolHeader = options.InitialProtocolHeader;
        _protocolLock = new SemaphoreSlim(1, 1);
        _writeLock = new SemaphoreSlim(1, 1);
    }

    public override AmqpProtocolHeader LocalProtocolHeader => _localProtocolHeader;

    public override AmqpProtocolHeader? RemoteProtocolHeader => _remoteProtocolHeader;

    public override EndPoint LocalEndPoint => _context.LocalEndPoint;

    public override EndPoint RemoteEndPoint => _context.RemoteEndPoint;

    public override ITransportConnectionPipe Pipe => _context.Pipe;

    public override IDictionary<string, object?> Items => _context.Items;

    public override async ValueTask<AmqpProtocolHeader> NegotiateAsync(CancellationToken cancellationToken = default)
    {
        if (_remoteProtocolHeader.HasValue)
        {
            return _remoteProtocolHeader.Value;
        }

        await _protocolLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (_remoteProtocolHeader.HasValue)
            {
                return _remoteProtocolHeader.Value;
            }

            await WriteProtocolHeaderAsync(_localProtocolHeader, cancellationToken).ConfigureAwait(false);
            _remoteProtocolHeader = await ReadProtocolHeaderAsync(cancellationToken).ConfigureAwait(false);

            return _remoteProtocolHeader.Value;
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
            _localProtocolHeader = protocolHeader;
            _remoteProtocolHeader = null;

            await WriteProtocolHeaderAsync(protocolHeader, cancellationToken).ConfigureAwait(false);
            _remoteProtocolHeader = await ReadProtocolHeaderAsync(cancellationToken).ConfigureAwait(false);

            return _remoteProtocolHeader.Value;
        }
        finally
        {
            _protocolLock.Release();
        }
    }

    public override async IAsyncEnumerable<AmqpFrame> ReceiveAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await EnsureNegotiatedAsync(cancellationToken).ConfigureAwait(false);

        PipeReader reader = Pipe.Input;

        while (true)
        {
            ReadResult result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            ReadOnlySequence<byte> buffer = result.Buffer;

            try
            {
                while (AmqpEncoding.TryReadFrame(ref buffer, _localProtocolHeader.FrameType, out AmqpFrame frame))
                {
                    yield return frame;
                }

                if (result.IsCompleted)
                {
                    if (buffer.Length > 0)
                    {
                        throw new AmqpProtocolException("The carrier stream ended with an incomplete AMQP frame.");
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
        await EnsureNegotiatedAsync(cancellationToken).ConfigureAwait(false);

        byte[] payload = AmqpEncoding.EncodeFrame(frame, _localProtocolHeader.FrameType, _options.MaxFrameSize);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await Pipe.Output.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            await Pipe.Output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async ValueTask EnsureNegotiatedAsync(CancellationToken cancellationToken)
    {
        if (_remoteProtocolHeader.HasValue)
        {
            return;
        }

        if (!_options.AutoNegotiateProtocolHeader)
        {
            throw new InvalidOperationException("AMQP protocol header negotiation must be performed before exchanging frames.");
        }

        await NegotiateAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask WriteProtocolHeaderAsync(AmqpProtocolHeader header, CancellationToken cancellationToken)
    {
        byte[] payload = AmqpEncoding.EncodeProtocolHeader(header);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await Pipe.Output.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            await Pipe.Output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async ValueTask<AmqpProtocolHeader> ReadProtocolHeaderAsync(CancellationToken cancellationToken)
    {
        PipeReader reader = Pipe.Input;

        while (true)
        {
            ReadResult result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            ReadOnlySequence<byte> buffer = result.Buffer;

            if (buffer.Length >= 8)
            {
                byte[] headerBytes = buffer.Slice(0, 8).ToArray();
                reader.AdvanceTo(buffer.GetPosition(8), buffer.End);
                return AmqpEncoding.DecodeProtocolHeader(headerBytes);
            }

            reader.AdvanceTo(buffer.Start, buffer.End);

            if (result.IsCompleted)
            {
                throw new AmqpProtocolException("The carrier stream ended before the AMQP protocol header was fully received.");
            }
        }
    }
}
