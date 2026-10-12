using System;

using Assimalign.Cohesion.Hosting.Resources;
using Assimalign.Cohesion.IdentityModel.Token.JsonWebToken;

namespace Assimalign.Cohesion.Web.Hosting.Resources.Internal;

/// <summary>
/// Verifies the default application-key bootstrap credential: an ES256 token issued by the ambient
/// application, for the ambient gateway, within the bootstrap lifetime ceiling.
/// </summary>
internal sealed class BootstrapTokenVerifier
{
    private readonly string _application;
    private readonly string _gateway;
    private readonly IJsonWebTokenValidator _validator;

    internal BootstrapTokenVerifier(ResourceContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _application = context.ApplicationName ?? throw new InvalidOperationException(
            "A gateway-managed resource requires an application name.");
        _gateway = context.GatewayName ?? throw new InvalidOperationException(
            "A gateway-managed resource requires a gateway name.");
        if (context.ApplicationTrustKey.IsEmpty)
        {
            throw new InvalidOperationException(
                "A gateway-managed resource requires a public application trust key.");
        }

        JsonWebKey key = ReadApplicationTrustKey(context.ApplicationTrustKey.Span);
        _validator = JsonWebTokenValidator.CreateEs256(new JsonWebTokenValidationProfile(
            _application,
            new JsonWebKeySet(key),
            ResourceCredentialProfile.BootstrapMaximumLifetime)
        {
            ClockSkew = ResourceCredentialProfile.ClockSkew,
            ExpectedSubject = _gateway,
        });
    }

    internal ControlPlaneCredentialVerification Validate(
        string compactToken,
        string expectedAudience,
        DateTimeOffset now)
    {
        if (!_validator.TryValidate(compactToken, now, out JsonWebToken? token))
        {
            return new(new ResourceCredentialVerification(ResourceCredentialStatus.Unauthorized, null, null),
                CredentialRejectionReason.InvalidToken);
        }

        var caller = new ResourceCaller(_application, _gateway, ResourceCallerKind.Gateway, []);
        return JsonWebTokenValidator.HasAudience(token, expectedAudience)
            ? new(new ResourceCredentialVerification(ResourceCredentialStatus.Authorized, caller, null),
                CredentialRejectionReason.None)
            : new(new ResourceCredentialVerification(ResourceCredentialStatus.Forbidden, caller, null),
                CredentialRejectionReason.AudienceMismatch);
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
}
