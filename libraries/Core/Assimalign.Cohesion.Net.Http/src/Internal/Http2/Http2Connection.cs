using Assimalign.Cohesion.Net.Transports;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Http.Internal;

internal class Http2Connection : HttpConnection
{
    // This uses C# compiler's ability to refer to static data directly. For more information see https://vcsjones.dev/2019/02/01/csharp-readonly-span-bytes-static
    // TODO: once C# 11 comes out want to switch to Utf8 Strings Literals 
    private static ReadOnlySpan<byte> ClientPreface => new byte[24] { (byte)'P', (byte)'R', (byte)'I', (byte)' ', (byte)'*', (byte)' ', (byte)'H', (byte)'T', (byte)'T', (byte)'P', (byte)'/', (byte)'2', (byte)'.', (byte)'0', (byte)'\r', (byte)'\n', (byte)'\r', (byte)'\n', (byte)'S', (byte)'M', (byte)'\r', (byte)'\n', (byte)'\r', (byte)'\n' };
    private static ReadOnlySpan<byte> Authority => new byte[10] { (byte)':', (byte)'a', (byte)'u', (byte)'t', (byte)'h', (byte)'o', (byte)'r', (byte)'i', (byte)'t', (byte)'y' };
    private static ReadOnlySpan<byte> Method => new byte[7] { (byte)':', (byte)'m', (byte)'e', (byte)'t', (byte)'h', (byte)'o', (byte)'d' };
    private static ReadOnlySpan<byte> Path => new byte[5] { (byte)':', (byte)'p', (byte)'a', (byte)'t', (byte)'h' };
    private static ReadOnlySpan<byte> Scheme => new byte[7] { (byte)':', (byte)'s', (byte)'c', (byte)'h', (byte)'e', (byte)'m', (byte)'e' };
    private static ReadOnlySpan<byte> Status => new byte[7] { (byte)':', (byte)'s', (byte)'t', (byte)'a', (byte)'t', (byte)'u', (byte)'s' };
    private static ReadOnlySpan<byte> Connection => new byte[10] { (byte)'c', (byte)'o', (byte)'n', (byte)'n', (byte)'e', (byte)'c', (byte)'t', (byte)'i', (byte)'o', (byte)'n' };
    private static ReadOnlySpan<byte> TeBytes => new byte[2] { (byte)'t', (byte)'e' };
    private static ReadOnlySpan<byte> Trailers => new byte[8] { (byte)'t', (byte)'r', (byte)'a', (byte)'i', (byte)'l', (byte)'e', (byte)'r', (byte)'s' };
    private static ReadOnlySpan<byte> Connect => new byte[7] { (byte)'C', (byte)'O', (byte)'N', (byte)'N', (byte)'E', (byte)'C', (byte)'T' };



    private readonly ConcurrentDictionary<int, Tuple<Http2ConnectionParsingStatus,  Http2Stream>> streams;

    internal enum Http2ConnectionParsingStatus
    {
        
    }

    public Http2Connection() 
    {
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
        throw new NotImplementedException();
    }
}
