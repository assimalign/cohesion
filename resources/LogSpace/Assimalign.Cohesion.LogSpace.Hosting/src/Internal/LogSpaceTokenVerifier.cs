using System;

using Assimalign.Cohesion.Hosting.Resources;
using Assimalign.Cohesion.IdentityModel;
using Assimalign.Cohesion.IdentityModel.Token.JsonWebToken;

namespace Assimalign.Cohesion.LogSpace.Hosting.Internal;

/// <summary>
/// Verifies the default application-key credentials a LogSpace accepts: gateway management tokens and
/// <c>scope=telemetry</c> emitter tokens whose subject names the emitting resource.
/// </summary>
internal sealed class LogSpaceTokenVerifier
{
    private readonly string _application;
    private readonly string _gateway;
    private readonly IJsonWebTokenValidator _validator;

    internal LogSpaceTokenVerifier(ResourceContext context)
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

        // The subject rule depends on the credential's scope, so it is applied below rather than by
        // the profile: management tokens name the gateway, telemetry tokens name the emitter.
        _validator = JsonWebTokenValidator.CreateEs256(new JsonWebTokenValidationProfile(
            _application,
            new JsonWebKeySet(ReadApplicationTrustKey(context.ApplicationTrustKey.Span)),
            ResourceCredentialProfile.BootstrapMaximumLifetime)
        {
            ClockSkew = ResourceCredentialProfile.ClockSkew,
            RequireSubject = false,
        });
    }

    internal ResourceCredentialVerification Validate(
        string compactToken,
        string expectedAudience,
        DateTimeOffset now,
        bool telemetry)
    {
        if (!_validator.TryValidate(compactToken, now, out JsonWebToken? token))
        {
            return new ResourceCredentialVerification(ResourceCredentialStatus.Unauthorized, null, null);
        }

        string? scope = token.Claims.GetString(ResourceCredentialProfile.ScopeClaim);
        ResourceCaller caller;
        if (telemetry)
        {
            if (scope != ResourceCredentialProfile.TelemetryScope || string.IsNullOrWhiteSpace(token.Subject?.Value))
            {
                return new ResourceCredentialVerification(ResourceCredentialStatus.Forbidden, null, null);
            }

            caller = new ResourceCaller(_application, token.Subject.Value, ResourceCallerKind.TelemetryEmitter, []);
        }
        else if (scope == ResourceCredentialProfile.TelemetryScope)
        {
            return new ResourceCredentialVerification(ResourceCredentialStatus.Forbidden, null, null);
        }
        else if (!string.Equals(token.Subject?.Value, _gateway, StringComparison.Ordinal))
        {
            return new ResourceCredentialVerification(ResourceCredentialStatus.Unauthorized, null, null);
        }
        else
        {
            caller = new ResourceCaller(_application, _gateway, ResourceCallerKind.Gateway, []);
        }

        return JsonWebTokenValidator.HasAudience(token, expectedAudience)
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
}
