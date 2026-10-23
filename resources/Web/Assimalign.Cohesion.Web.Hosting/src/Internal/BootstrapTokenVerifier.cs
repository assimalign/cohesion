using System;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;

using Assimalign.Cohesion.Hosting.Resources;
using Assimalign.Cohesion.IdentityModel.Token;
using Assimalign.Cohesion.IdentityModel.Token.JsonWebToken;
using Assimalign.Cohesion.IdentityModel.Token.JsonWebToken.Internal;

namespace Assimalign.Cohesion.Web.Hosting.Internal;

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
    private readonly JsonWebKey _key;
    private readonly object _sync = new();
    private ECDsa? _publicKey;
    private IJsonWebTokenSignatureVerifier? _signatureVerifier;
    private int _owners;

    internal BootstrapTokenVerifier(ResourceContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _application = context.ApplicationName ?? throw new InvalidOperationException(
            ErrorMessages.ManagedResourceApplicationNameRequired);
        _gateway = context.GatewayName ?? throw new InvalidOperationException(
            ErrorMessages.ManagedResourceGatewayNameRequired);
        if (context.ApplicationTrustKey.IsEmpty)
        {
            throw new InvalidOperationException(
                ErrorMessages.ManagedResourceTrustKeyRequired);
        }

        JsonWebKey key = ReadApplicationTrustKey(context.ApplicationTrustKey.Span);
        _key = key;
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
        JsonWebToken? token;
        bool valid;
        lock (_sync)
        {
            // ECDsa implementations need not support concurrent use. The same lock serializes
            // verification with the last owner's disposal, without spanning an asynchronous call.
            valid = Es256JsonWebTokenValidation.TryValidate(
                _validator.Profile, compactToken, now, _signatureVerifier, out token);
        }
        if (!valid)
        {
            return new(new ResourceCredentialVerification(ResourceCredentialStatus.Unauthorized, null, null),
                CredentialRejectionReason.InvalidToken);
        }

        var caller = new ResourceCaller(_application, _gateway, ResourceCallerKind.Gateway, []);
        // The extension rejects null/blank audiences; retain the previous ordinal match for them.
        bool hasAudience = string.IsNullOrWhiteSpace(expectedAudience)
            ? token!.Audiences.Contains(expectedAudience!, StringComparer.Ordinal)
            : token!.HasAudience(expectedAudience);
        return hasAudience
            ? new(new ResourceCredentialVerification(ResourceCredentialStatus.Authorized, caller, null),
                CredentialRejectionReason.None)
            : new(new ResourceCredentialVerification(ResourceCredentialStatus.Forbidden, caller, null),
                CredentialRejectionReason.AudienceMismatch);
    }

    internal void Retain()
    {
        lock (_sync)
        {
            if (_owners == 0)
            {
                ECDsa? publicKey = _key.CreateECDsa();
                try
                {
                    _signatureVerifier = EcdsaJsonWebTokenSignatureVerifier.Create(publicKey, _key.KeyId);
                    _publicKey = publicKey;
                    publicKey = null;
                }
                finally
                {
                    publicKey?.Dispose();
                }
            }
            _owners++;
        }
    }

    internal void Release()
    {
        lock (_sync)
        {
            if (--_owners == 0)
            {
                _signatureVerifier = null;
                _publicKey?.Dispose();
                _publicKey = null;
            }
        }
    }

    private static JsonWebKey ReadApplicationTrustKey(ReadOnlySpan<byte> applicationTrustKey)
    {
        string? failure = null;
        return JsonWebKey.TryParse(applicationTrustKey, out JsonWebKey? key) &&
            key.TryValidateEcdsaVerificationKey("P-256", out failure)
                ? key
                : throw new InvalidOperationException(
                    string.Format(CultureInfo.CurrentCulture, ErrorMessages.ApplicationTrustKeyInvalid, failure).TrimEnd());
    }

    private sealed class DebugView
    {
        public DebugView(BootstrapTokenVerifier value)
        {
        }
    }
}
