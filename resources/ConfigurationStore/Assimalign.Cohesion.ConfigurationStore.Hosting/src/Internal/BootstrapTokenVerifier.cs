using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Hosting.Resources;
using Assimalign.Cohesion.IdentityModel;
using Assimalign.Cohesion.IdentityModel.Token.JsonWebToken;

namespace Assimalign.Cohesion.ConfigurationStore.Hosting.Internal;

/// <summary>
/// Verifies the default application-key credential against the store's trusted issuers: the ambient
/// application's gateway and any peer issuer persisted in <c>trust/trusted-issuers.json</c>.
/// </summary>
internal sealed class BootstrapTokenVerifier
{
    private readonly IReadOnlyList<ConfigurationTrustedIssuer> _trustedIssuers;
    private readonly string? _application;
    private readonly IJsonWebTokenValidator _validator;

    internal BootstrapTokenVerifier(IReadOnlyList<ConfigurationTrustedIssuer> trustedIssuers, string? application)
    {
        _trustedIssuers = trustedIssuers ?? throw new ArgumentNullException(nameof(trustedIssuers));
        _application = application;
        _validator = JsonWebTokenValidator.CreateEs256(new JsonWebTokenValidationProfile(
            issuer => FindIssuer(issuer)?.Keys,
            ResourceCredentialProfile.BootstrapMaximumLifetime)
        {
            ClockSkew = ResourceCredentialProfile.ClockSkew,
        });
    }

    internal ResourceCredentialVerification Validate(
        string compactToken,
        string expectedAudience,
        DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedAudience);

        if (!_validator.TryValidate(compactToken, now, out JsonWebToken? token) ||
            FindIssuer(token.Issuer!) is not ConfigurationTrustedIssuer issuer)
        {
            return new ResourceCredentialVerification(ResourceCredentialStatus.Unauthorized, null, null);
        }

        // A telemetry-emitter credential authorizes ingestion at a sink and nothing else. The store
        // is never an ingestion endpoint, so the scope is refused on every route even when an
        // application registers the store as its telemetry sink.
        if (token.Claims.GetString(ResourceCredentialProfile.ScopeClaim) == ResourceCredentialProfile.TelemetryScope)
        {
            return new ResourceCredentialVerification(ResourceCredentialStatus.Forbidden, null, null);
        }

        // The store's own application is its gateway and every other trusted issuer is a peer.
        var caller = new ResourceCaller(
            issuer.Issuer,
            token.Subject!.Value,
            string.Equals(issuer.Issuer, _application, StringComparison.Ordinal)
                ? ResourceCallerKind.Gateway
                : ResourceCallerKind.Peer,
            []);
        return JsonWebTokenValidator.HasAudience(token, expectedAudience)
            ? new ResourceCredentialVerification(ResourceCredentialStatus.Authorized, caller, null)
            : new ResourceCredentialVerification(ResourceCredentialStatus.Forbidden, caller, null);
    }

    private ConfigurationTrustedIssuer? FindIssuer(string issuerName)
    {
        for (int index = 0; index < _trustedIssuers.Count; index++)
        {
            if (string.Equals(_trustedIssuers[index].Issuer, issuerName, StringComparison.Ordinal))
            {
                return _trustedIssuers[index];
            }
        }

        return null;
    }
}
