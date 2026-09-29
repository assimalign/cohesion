using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Internal;

/// <summary>
/// Binds an <see cref="IAuthenticatedControlPlaneClient"/> to the calling application's
/// peer-control-plane credential and trusted-issuer snapshot for one external resolution.
/// </summary>
internal sealed class AuthenticatedControlPlaneClient : IControlPlaneClient
{
    private readonly IAuthenticatedControlPlaneClient _client;
    private readonly Func<CancellationToken, ValueTask<string>> _issueBearerToken;
    private readonly IReadOnlyList<TrustedIssuer> _trustedIssuers;
    private string? _bearerToken;

    /// <summary>
    /// Initializes a new instance of the <see cref="AuthenticatedControlPlaneClient"/> class.
    /// </summary>
    /// <param name="client">The authenticated peer client.</param>
    /// <param name="issueBearerToken">
    /// Mints the calling application's peer-control-plane bearer credential. It runs on the first
    /// call, so a registered credential issuer is consulted asynchronously; the credential is then
    /// reused for this client's lifetime.
    /// </param>
    /// <param name="trustedIssuers">The calling application's trusted-issuer snapshot.</param>
    public AuthenticatedControlPlaneClient(
        IAuthenticatedControlPlaneClient client,
        Func<CancellationToken, ValueTask<string>> issueBearerToken,
        IReadOnlyList<TrustedIssuer> trustedIssuers)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _issueBearerToken = issueBearerToken ?? throw new ArgumentNullException(nameof(issueBearerToken));
        _trustedIssuers = trustedIssuers ?? throw new ArgumentNullException(nameof(trustedIssuers));
    }

    public async ValueTask<ApplicationExportDocument> GetApplicationAsync(
        Uri address,
        CancellationToken cancellationToken = default)
    {
        string bearerToken = _bearerToken ??= await _issueBearerToken(cancellationToken).ConfigureAwait(false);
        return await _client
            .GetApplicationAsync(
                address,
                bearerToken,
                _trustedIssuers,
                cancellationToken)
            .ConfigureAwait(false);
    }
}
