using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Transports;

namespace Assimalign.Cohesion.Http.Transports.Internal.Http3;

using Assimalign.Cohesion.Http.Transports.Internal.Http3.Frames;

internal sealed class Http3ConnectionContext : HttpConnectionContext
{
    private static readonly ITransportConnectionPipe DisabledPipe = new TransportConnectionPipe(Stream.Null);
    private readonly IMultiplexTransportConnection _connection;
    private readonly Dictionary<string, object?> _items;
    private readonly bool _isSecure;
    private EndPoint _localEndPoint;
    private EndPoint _remoteEndPoint;

    [SupportedOSPlatform("windows")]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    [SupportedOSPlatform("osx")]
    public Http3ConnectionContext(IMultiplexTransportConnection connection, bool isSecure)
    {
        _connection = connection;
        _isSecure = isSecure;
        _items = new Dictionary<string, object?>(StringComparer.Ordinal);
        _localEndPoint = connection is QuicTransportConnection quicConnection
            ? quicConnection.LocalEndPoint
            : new IPEndPoint(IPAddress.None, 0);
        _remoteEndPoint = connection is QuicTransportConnection quicConnection2
            ? quicConnection2.RemoteEndPoint
            : new IPEndPoint(IPAddress.None, 0);
    }

    public override EndPoint LocalEndPoint => _localEndPoint;

    public override EndPoint RemoteEndPoint => _remoteEndPoint;

    public override ITransportConnectionPipe Pipe => DisabledPipe;

    public override IDictionary<string, object?> Items => _items;

    public override async IAsyncEnumerable<IHttpContext> ReceiveAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            ITransportConnectionContext streamContext;

            try
            {
                streamContext = await _connection.OpenInboundAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }

            _localEndPoint = streamContext.LocalEndPoint;
            _remoteEndPoint = streamContext.RemoteEndPoint;

            Http3Context? context = await ReadRequestAsync(streamContext, cancellationToken).ConfigureAwait(false);

            if (context is not null)
            {
                yield return context;
            }
        }
    }

    public override async ValueTask SendAsync(IHttpContext context, CancellationToken cancellationToken = default)
    {
        if (context is not Http3Context http3Context)
        {
            throw new InvalidOperationException("The supplied context does not belong to an HTTP/3 connection.");
        }

        Stream stream = http3Context.StreamContext.Pipe.GetStream();
        byte[] bodyBytes = await ReadBodyAsync(http3Context.Response.Body, cancellationToken).ConfigureAwait(false);
        byte[] headerBlock = Http3HeaderCodec.EncodeResponseHeaders(http3Context, bodyBytes);

        await WriteFrameAsync(stream, Http3FrameType.Headers, headerBlock, cancellationToken).ConfigureAwait(false);

        if (bodyBytes.Length > 0)
        {
            await WriteFrameAsync(stream, Http3FrameType.Data, bodyBytes, cancellationToken).ConfigureAwait(false);
        }

        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<Http3Context?> ReadRequestAsync(ITransportConnectionContext streamContext, CancellationToken cancellationToken)
    {
        Stream stream = streamContext.Pipe.GetStream();
        using MemoryStream requestBuffer = new();
        await stream.CopyToAsync(requestBuffer, cancellationToken).ConfigureAwait(false);

        byte[] requestBytes = requestBuffer.ToArray();
        byte[]? headerBlock = null;
        using MemoryStream body = new();
        int index = 0;

        while (index < requestBytes.Length)
        {
            long frameType = QuicVariableLengthInteger.Decode(requestBytes, ref index);
            long frameLength = QuicVariableLengthInteger.Decode(requestBytes, ref index);
            byte[] payload = requestBytes.AsSpan(index, checked((int)frameLength)).ToArray();
            index += checked((int)frameLength);

            switch ((Http3FrameType)frameType)
            {
                case Http3FrameType.Headers:
                    headerBlock = payload;
                    break;
                case Http3FrameType.Data:
                    await body.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
                    break;
            }
        }

        if (headerBlock is null)
        {
            return null;
        }

        byte[] bodyBytes = body.ToArray();
        Http3Request request = Http3HeaderCodec.DecodeRequestHeaders(headerBlock, _isSecure ? HttpScheme.Https : HttpScheme.Http, bodyBytes);
        Http3Response response = new();
        HttpConnectionInfo connectionInfo = new(streamContext.LocalEndPoint, streamContext.RemoteEndPoint, _isSecure);

        return new Http3Context(request, response, connectionInfo, cancellationToken, streamContext);
    }

    private static async Task WriteFrameAsync(Stream stream, Http3FrameType frameType, byte[] payload, CancellationToken cancellationToken)
    {
        QuicVariableLengthInteger.Write(stream, (long)frameType);
        QuicVariableLengthInteger.Write(stream, payload.Length);

        if (payload.Length > 0)
        {
            await stream.WriteAsync(payload, 0, payload.Length, cancellationToken).ConfigureAwait(false);
        }
    }
    private static async Task<byte[]> ReadBodyAsync(Stream body, CancellationToken cancellationToken)
    {
        if (body is MemoryStream memoryStream)
        {
            return memoryStream.ToArray();
        }

        using MemoryStream copy = new();
        if (body.CanSeek)
        {
            body.Position = 0;
        }

        await body.CopyToAsync(copy, cancellationToken).ConfigureAwait(false);
        return copy.ToArray();
    }
}
