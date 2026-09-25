using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Internal;

internal sealed class AuthenticatedControlPlaneClient : IControlPlaneClient
{
    private readonly IAuthenticatedControlPlaneClient _client;
    private readonly string _bearerToken;
    private readonly IReadOnlyList<TrustedIssuer> _trustedIssuers;

    public AuthenticatedControlPlaneClient(
        IAuthenticatedControlPlaneClient client,
        string bearerToken,
        IReadOnlyList<TrustedIssuer> trustedIssuers)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _bearerToken = bearerToken ?? throw new ArgumentNullException(nameof(bearerToken));
        _trustedIssuers = trustedIssuers ?? throw new ArgumentNullException(nameof(trustedIssuers));
    }

    public ValueTask<ApplicationExportDocument> GetApplicationAsync(
        Uri address,
        CancellationToken cancellationToken = default) =>
        _client.GetApplicationAsync(
            address,
            _bearerToken,
            _trustedIssuers,
            cancellationToken);
}
