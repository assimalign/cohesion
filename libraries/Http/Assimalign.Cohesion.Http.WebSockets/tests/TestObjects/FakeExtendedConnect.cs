using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http.WebSockets.Tests.TestObjects;

/// <summary>
/// Stands in for the HTTP/2 and HTTP/3 transports' extended CONNECT feature: accepting records the
/// response status and headers staged at that moment, which is what the real transport sends on the
/// <c>200</c>, and surrenders the configured tunnel stream.
/// </summary>
internal sealed class FakeExtendedConnect : IHttpExtendedConnectFeature
{
    private readonly Stream _tunnel;
    private readonly IHttpResponse _response;

    public FakeExtendedConnect(string protocol, Stream tunnel, IHttpResponse response)
    {
        Protocol = protocol;
        _tunnel = tunnel;
        _response = response;
    }

    /// <summary>Gets how many times the tunnel was accepted.</summary>
    public int AcceptCount { get; private set; }

    /// <summary>Gets the response headers as they stood when the tunnel was accepted.</summary>
    public IReadOnlyDictionary<string, string> AcceptedHeaders { get; private set; } = new Dictionary<string, string>();

    public string Name => "Assimalign.Cohesion.Http.Connections.ExtendedConnect";

    public string Protocol { get; }

    public ValueTask<Stream> AcceptAsync(CancellationToken cancellationToken = default)
    {
        AcceptCount++;

        Dictionary<string, string> snapshot = new(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<HttpHeaderKey, HttpHeaderValue> header in _response.Headers)
        {
            snapshot[header.Key.Value] = header.Value.Value;
        }

        AcceptedHeaders = snapshot;
        return ValueTask.FromResult(_tunnel);
    }
}
