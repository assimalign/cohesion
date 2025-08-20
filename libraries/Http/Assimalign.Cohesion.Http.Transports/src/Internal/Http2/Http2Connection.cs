using Assimalign.Cohesion.Transports;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http.Internal;

internal class Http2Connection : HttpConnection
{
    // This uses C# compiler's ability to refer to static data directly. For more information see https://vcsjones.dev/2019/02/01/csharp-readonly-span-bytes-static
    // TODO: once C# 11 comes out want to switch to Utf8 Strings Literals 
    private static ReadOnlySpan<byte> ClientPreface => "PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n"u8;
    private static ReadOnlySpan<byte> Authority => ":authority"u8;
    private static ReadOnlySpan<byte> Method => ":method"u8;
    private static ReadOnlySpan<byte> Path => ":path"u8;
    private static ReadOnlySpan<byte> Scheme => ":scheme"u8;
    private static ReadOnlySpan<byte> Status => ":status"u8;
    private static ReadOnlySpan<byte> Connection => "connection"u8;
    private static ReadOnlySpan<byte> TeBytes => "te"u8;
    private static ReadOnlySpan<byte> Trailers => "trailers"u8;
    private static ReadOnlySpan<byte> Connect => "CONNECT"u8;
    private static ReadOnlySpan<byte> Protocol => ":protocol"u8;


    private readonly ConcurrentDictionary<int, Tuple<Http2ConnectionParsingStatus,  Http2Stream>> streams;

    internal enum Http2ConnectionParsingStatus
    {
        
    }

    private readonly ITransportConnectionContext _transportConnectionContext;

    public Http2Connection(ITransportConnectionContext transportConnectionContext) 
    {
        _transportConnectionContext = transportConnectionContext;
        this.streams = new();
    }


    //protected override IAsyncEnumerable<IHttpContext> ReceiveAsync(CancellationToken cancellationToken = default)
    //{
    //    throw new NotImplementedException();
    //}

    //protected override Task<IHttpContext> SendAsync(IHttpContext context, CancellationToken cancellationToken = default)
    //{
    //    throw new NotImplementedException();
    //}

    internal override IAsyncEnumerable<IHttpContext> ProcessAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        _transportConnectionContext.Pipe.ReadAsync();
        
        throw new NotImplementedException();
    }

    public override IAsyncEnumerable<IHttpContext> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }




    public override IAsyncEnumerable<IHttpContext> SendAsync(IHttpContext context, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public override void Dispose()
    {
        throw new NotImplementedException();
    }
}
