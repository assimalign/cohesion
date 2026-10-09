using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http.Internal;

/// <summary>
/// Default <see cref="IHttpExtendedConnectFeature"/> implementation. Installed by
/// <see cref="HttpExtendedConnectInterceptor"/>'s request-head hook on an extended CONNECT, and bound
/// by its response hook to the exchange control whose
/// <see cref="IHttpExchangeControl.AcceptTunnelAsync"/> performs the accept. Ordinary exchanges carry
/// no such feature, so the accessor reads <see langword="null"/>.
/// </summary>
/// <remarks>
/// Every accept rule — accept once, never after the response started or the exchange was cancelled,
/// fault on a stream already reset — is the control's, so the feature adds none and the guards run
/// in one order on every path.
/// </remarks>
internal sealed class HttpExtendedConnectFeature : IHttpExtendedConnectFeature
{
    /// <summary>The name under which the extended CONNECT feature is registered.</summary>
    public const string FeatureName = "Assimalign.Cohesion.Http.ExtendedConnect";

    private IHttpExchangeControl? _control;

    /// <summary>
    /// Initializes the feature for an extended CONNECT exchange.
    /// </summary>
    /// <param name="protocol">The validated <c>:protocol</c> the client requested; never <see langword="null"/> or empty.</param>
    public HttpExtendedConnectFeature(string protocol)
    {
        Protocol = protocol;
    }

    /// <inheritdoc />
    public string Name => FeatureName;

    /// <inheritdoc />
    public string Protocol { get; }

    /// <summary>
    /// Binds the feature to the exchange control that accepts the tunnel. Called once, from the
    /// interceptor's response hook, before the application observes the exchange.
    /// </summary>
    /// <param name="control">The transport's exchange control.</param>
    public void Bind(IHttpExchangeControl control)
    {
        _control = control;
    }

    /// <inheritdoc />
    public ValueTask<Stream> AcceptAsync(CancellationToken cancellationToken = default)
    {
        if (_control is not { } control)
        {
            // Only reachable when the transport never ran the exchange's response phase, so no
            // control exists to perform the accept.
            return ValueTask.FromException<Stream>(new InvalidOperationException(
                "The extended CONNECT tunnel cannot be accepted: the exchange offers no exchange control."));
        }

        return control.AcceptTunnelAsync(cancellationToken);
    }
}
