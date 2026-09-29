using System;

using Assimalign.Cohesion.Hosting.Resources;
using Assimalign.Cohesion.IdentityModel;
using Assimalign.Cohesion.IdentityModel.Token.JsonWebToken;

namespace Assimalign.Cohesion.SecretStore.Hosting.Internal;

/// <summary>
/// Verifies the default application-key credential against the store's trusted issuers: the ambient
/// application's gateway and every peer issuer granted through <c>cohesion.trust.add</c>.
/// </summary>
internal sealed class BootstrapTokenVerifier
{
    private readonly TrustedIssuerStore _trustedIssuers;
    private readonly string? _application;
    private readonly IJsonWebTokenValidator _validator;

    internal BootstrapTokenVerifier(TrustedIssuerStore trustedIssuers, string? application)
    {
        _trustedIssuers = trustedIssuers ?? throw new ArgumentNullException(nameof(trustedIssuers));
        _application = application;
        _validator = JsonWebTokenValidator.CreateEs256(new JsonWebTokenValidationProfile(
            issuer => _trustedIssuers.Find(issuer)?.Keys,
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

        if (!_validator.TryValidate(compactToken, now, out JsonWebToken? token))
        {
            return new ResourceCredentialVerification(ResourceCredentialStatus.Unauthorized, null, null);
        }

        // The store's own application is its gateway and every other trusted issuer is a peer
        // application. Issuers are only ever added or re-keyed, never removed, so a validated
        // token's issuer still resolves here; a re-key between validation and this lookup is caught by
        // re-selecting the verifying key (kid is the key's RFC 7638 thumbprint), so the caller is always
        // mapped from the entry whose key verified the signature.
        if (_trustedIssuers.Find(token.Issuer!) is not TrustedIssuer issuer ||
            issuer.Keys.Find(token.Header.KeyId) is null)
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

        var caller = new ResourceCaller(
            issuer.Issuer,
            token.Subject!.Value,
            string.Equals(issuer.Issuer, _application, StringComparison.Ordinal)
                ? ResourceCallerKind.Gateway
                : ResourceCallerKind.Peer,
            issuer.AllowedCommandKinds);
        return JsonWebTokenValidator.HasAudience(token, expectedAudience)
            ? new ResourceCredentialVerification(ResourceCredentialStatus.Authorized, caller, null)
            : new ResourceCredentialVerification(ResourceCredentialStatus.Forbidden, caller, null);
    }
}
