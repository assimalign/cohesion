using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Hosting.Resources;
using Assimalign.Cohesion.IdentityModel;
using Assimalign.Cohesion.IdentityModel.Token.JsonWebToken;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.ControlPlane.Internal;

/// <summary>
/// Verifies the ES256 developer and peer credentials presented to the gateway control plane: issued by a
/// trusted issuer for the <c>cohesion-export</c> audience within the developer lifetime ceiling.
/// </summary>
internal static class ControlPlaneTokenVerifier
{
    public static bool TryVerify(
        string compactToken,
        IReadOnlyList<TrustedIssuer> trustedIssuers,
        DateTimeOffset now,
        out ControlPlanePrincipal principal)
    {
        principal = default;
        IJsonWebTokenValidator validator = JsonWebTokenValidator.CreateEs256(new JsonWebTokenValidationProfile(
            issuer => FindIssuer(issuer, trustedIssuers) is { } trusted &&
                JsonWebKey.TryParse(trusted.PublicKey, out JsonWebKey? key)
                    ? new JsonWebKeySet(key)
                    : null,
            ResourceCredentialProfile.DeveloperMaximumLifetime)
        {
            ClockSkew = ResourceCredentialProfile.ClockSkew,
        });
        if (!validator.TryValidate(compactToken, now, out JsonWebToken? token) ||
            !JsonWebTokenValidator.HasAudience(token, ResourceCredentialProfile.ExportAudience) ||
            FindIssuer(token.Issuer!, trustedIssuers) is not TrustedIssuer issuer)
        {
            return false;
        }

        principal = new ControlPlanePrincipal(
            token.Issuer!,
            token.Subject!.Value,
            string.Equals(
                token.Claims.GetString(ResourceCredentialProfile.TokenUseClaim),
                ResourceCredentialProfile.GatewayTokenUse,
                StringComparison.Ordinal),
            issuer.AllowedCommandKinds);
        return true;
    }

    private static TrustedIssuer? FindIssuer(
        string issuerName,
        IReadOnlyList<TrustedIssuer> trustedIssuers)
    {
        for (int index = 0; index < trustedIssuers.Count; index++)
        {
            TrustedIssuer issuer = trustedIssuers[index];
            if (string.Equals(issuer.Issuer, issuerName, StringComparison.Ordinal))
            {
                return issuer;
            }
        }

        return null;
    }
}

internal readonly record struct ControlPlanePrincipal(
    string Issuer,
    string Subject,
    bool CanDispatchCommands,
    IReadOnlyList<string> AllowedCommandKinds);
