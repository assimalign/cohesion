using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.WebSockets.Tests.TestObjects;

/// <summary>
/// Stands in for the protocol-upgrade interceptor's feature and its HTTP/1.1 upgrade to
/// <c>websocket</c>: accepting records the response headers staged at that moment, which the real
/// upgrade writes on the <c>101</c>, and surrenders the configured transport stream.
/// </summary>
internal sealed class FakeUpgradeFeature : IHttpProtocolUpgrade, IHttpProtocolUpgradeFeature
{
    private readonly Stream _transport;
    private readonly IHttpHeaderCollection _responseHeaders;

    public FakeUpgradeFeature(Stream transport, IHttpHeaderCollection responseHeaders)
    {
        _transport = transport;
        _responseHeaders = responseHeaders;
    }

    /// <summary>Gets how many times the upgrade was accepted.</summary>
    public int AcceptCount { get; private set; }

    /// <summary>Gets the response headers as they stood when the upgrade was accepted.</summary>
    public IReadOnlyDictionary<string, string> AcceptedHeaders { get; private set; } = new Dictionary<string, string>();

    public string Name => "Assimalign.Cohesion.Http.ProtocolUpgrade";

    public IHttpProtocolUpgrade Upgrade => this;

    public HttpProtocolUpgradeKind Kind => HttpProtocolUpgradeKind.Upgrade;

    public string? Protocol => "websocket";

    public ValueTask<Stream> AcceptAsync(CancellationToken cancellationToken = default)
    {
        AcceptCount++;

        Dictionary<string, string> snapshot = new(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<HttpHeaderKey, HttpHeaderValue> header in _responseHeaders)
        {
            snapshot[header.Key.Value] = header.Value.Value;
        }

        AcceptedHeaders = snapshot;
        return ValueTask.FromResult(_transport);
    }
}
