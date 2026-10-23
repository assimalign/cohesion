using System;
using System.Linq;

using Assimalign.Cohesion.Hosting.Resources;
using Assimalign.Cohesion.IdentityModel.Token;
using Assimalign.Cohesion.IdentityModel.Token.JsonWebToken;

namespace Assimalign.Cohesion.Scheduler.Hosting.Internal;

/// <summary>
/// Verifies the default application-key bootstrap credential: an ES256 token issued by the ambient
/// application, for the ambient gateway, within the bootstrap lifetime ceiling.
/// </summary>
[global::System.Diagnostics.DebuggerDisplay("BootstrapTokenVerifier")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed class BootstrapTokenVerifier
{
    private readonly string _application;
    private readonly string _gateway;
    private readonly IJsonWebTokenValidator _validator;

    internal BootstrapTokenVerifier(ResourceContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _application = context.ApplicationName ?? throw new InvalidOperationException(
            "A gateway-managed Scheduler requires an application name.");
        _gateway = context.GatewayName ?? throw new InvalidOperationException(
            "A gateway-managed Scheduler requires a gateway name.");
        if (context.ApplicationTrustKey.IsEmpty)
        {
            throw new InvalidOperationException(
                "A gateway-managed Scheduler requires a public application trust key.");
        }

        _validator = JsonWebTokenValidator.CreateEs256(new JsonWebTokenValidationProfile(
            _application,
            new JsonWebKeySet(ReadApplicationTrustKey(context.ApplicationTrustKey.Span)),
            ResourceCredentialProfile.BootstrapMaximumLifetime)
        {
            ClockSkew = ResourceCredentialProfile.ClockSkew,
            ExpectedSubject = _gateway,
        });
    }

    internal ResourceCredentialVerification Validate(
        string compactToken,
        string expectedAudience,
        DateTimeOffset now)
    {
        if (!_validator.TryValidate(compactToken, now, out JsonWebToken? token))
        {
            return new ResourceCredentialVerification(ResourceCredentialStatus.Unauthorized, null, null);
        }

        var caller = new ResourceCaller(_application, _gateway, ResourceCallerKind.Gateway, []);
        // The extension rejects null/blank audiences; retain the previous ordinal match for them.
        bool hasAudience = string.IsNullOrWhiteSpace(expectedAudience)
            ? token.Audiences.Contains(expectedAudience!, StringComparer.Ordinal)
            : token.HasAudience(expectedAudience);
        return hasAudience
            ? new ResourceCredentialVerification(ResourceCredentialStatus.Authorized, caller, null)
            : new ResourceCredentialVerification(ResourceCredentialStatus.Forbidden, caller, null);
    }

    private static JsonWebKey ReadApplicationTrustKey(ReadOnlySpan<byte> applicationTrustKey)
    {
        string? failure = null;
        return JsonWebKey.TryParse(applicationTrustKey, out JsonWebKey? key) &&
            key.TryValidateEcdsaVerificationKey("P-256", out failure)
                ? key
                : throw new InvalidOperationException(
                    $"The application trust key is not a valid EC P-256 JWK. {failure}".TrimEnd());
    }

    private sealed class DebugView
    {
        public DebugView(BootstrapTokenVerifier value)
        {
        }
    }
}
