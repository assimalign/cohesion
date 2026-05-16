using System.IO;
using System.Threading;

namespace Assimalign.Cohesion.Http.Transports.Internal.Http1;

internal sealed class Http1Context : TransportHttpContext
{
    private readonly Http1ProtocolUpgrade? _upgrade;

    public Http1Context(
        Http1Request request,
        Http1Response response,
        HttpConnectionInfo connectionInfo,
        CancellationToken requestAborted,
        bool keepAlive,
        Stream? transportStream = null,
        HttpProtocolUpgradeKind upgradeKind = HttpProtocolUpgradeKind.None,
        string? upgradeProtocol = null)
        : base(HttpVersion.Http11, request, response, connectionInfo, requestAborted)
    {
        KeepAlive = keepAlive;

        if (upgradeKind != HttpProtocolUpgradeKind.None && transportStream is not null)
        {
            _upgrade = new Http1ProtocolUpgrade(this, transportStream, upgradeKind, upgradeProtocol);
        }
    }

    public bool KeepAlive { get; set; }

    /// <summary>
    /// Gets or sets a flag indicating whether <see cref="Http1ProtocolUpgrade"/> has
    /// already emitted a response for this exchange. When <see langword="true"/>, the
    /// connection-level <see cref="Http1ConnectionContext.SendAsync"/> path skips
    /// writing so the upgrade response is not duplicated on the wire.
    /// </summary>
    public bool ResponseFinalized { get; set; }

    /// <inheritdoc />
    public override IHttpProtocolUpgrade? Upgrade => _upgrade;
}
