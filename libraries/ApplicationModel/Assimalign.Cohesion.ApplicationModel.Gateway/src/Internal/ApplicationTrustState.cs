using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;

using Assimalign.Cohesion.Hosting.Resources;
using Assimalign.Cohesion.IdentityModel;
using Assimalign.Cohesion.IdentityModel.Token.JsonWebToken;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Internal;

/// <summary>
/// One application's trust state: its ES256 signing key, its trusted-issuer snapshot, and the
/// gateway's default application-key credential issuer.
/// </summary>
internal sealed class ApplicationTrustState : IDisposable
{
    private const string bearerScheme = "Bearer";

    private readonly object _gate = new();
    private readonly Dictionary<string, TrustedIssuer> _issuers = new(StringComparer.Ordinal);
    private GatewayTrustKey _key;

    public ApplicationTrustState(ApplicationName application, ResourceName gateway, GatewayTrustKey key)
    {
        Application = application;
        Gateway = gateway;
        _key = key ?? throw new ArgumentNullException(nameof(key));
        AddOrReplace(new TrustedIssuer(Application.ToString(), _key.PublicJwk));
    }

    public ApplicationName Application { get; }

    public ResourceName Gateway { get; }

    public JsonElement PublicJwk
    {
        get
        {
            lock (_gate)
            {
                return _key.PublicJwk.Clone();
            }
        }
    }

    public ReadOnlyMemory<byte> PublicKey
    {
        get
        {
            lock (_gate)
            {
                return Encoding.UTF8.GetBytes(_key.PublicJwk.GetRawText());
            }
        }
    }

    /// <summary>
    /// The gateway's default ES256 application-key issuer. Every purpose keeps the claim set the
    /// gateway minted before credential issuers existed: gateway-to-gateway purposes
    /// (<see cref="ApplicationCredentialPurpose.RemoteCommand"/> and
    /// <see cref="ApplicationCredentialPurpose.PeerControlPlane"/>) add the gateway token-use claim,
    /// <see cref="ApplicationCredentialPurpose.Telemetry"/> adds the telemetry scope, and the rest carry
    /// only the registered claims.
    /// </summary>
    /// <param name="request">The credential to mint; its application must be this state's application.</param>
    /// <param name="now">The issue instant.</param>
    /// <returns>A <c>Bearer</c> credential that expires <see cref="ApplicationCredentialRequest.Lifetime"/> after <paramref name="now"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The request names another application, or its audience or subject is blank.</exception>
    public ApplicationCredential Issue(ApplicationCredentialRequest request, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Application != Application)
        {
            throw new ArgumentException(
                $"Application '{Application}' cannot issue a credential for application '{request.Application}'.",
                nameof(request));
        }

        string token = Issue(
            request.Audience,
            request.Subject,
            request.Lifetime,
            now,
            allowControlPlaneCommands: request.Purpose is
                ApplicationCredentialPurpose.RemoteCommand or ApplicationCredentialPurpose.PeerControlPlane,
            telemetry: request.Purpose == ApplicationCredentialPurpose.Telemetry);
        return new ApplicationCredential(bearerScheme, token, now.Add(request.Lifetime));
    }

    private string Issue(
        string audience,
        string subject,
        TimeSpan lifetime,
        DateTimeOffset now,
        bool allowControlPlaneCommands,
        bool telemetry)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(audience);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);

        lock (_gate)
        {
            var descriptor = new JsonWebTokenDescriptor
            {
                Id = Guid.NewGuid().ToString("N"),
                Issuer = Application.ToString(),
                Subject = new SubjectIdentifier(subject, issuer: Application.ToString()),
                TokenType = "JWT",
                IssuedAt = now,
                NotBefore = now,
                ExpiresAt = now.Add(lifetime),
            };
            descriptor.Audiences.Add(audience);
            if (telemetry)
            {
                descriptor.Claims.Add(new IdentityClaim(
                    ResourceCredentialProfile.ScopeClaim,
                    ResourceCredentialProfile.TelemetryScope));
            }
            if (allowControlPlaneCommands)
            {
                descriptor.Claims.Add(new IdentityClaim(
                    ResourceCredentialProfile.TokenUseClaim,
                    ResourceCredentialProfile.GatewayTokenUse));
            }

            return JsonWebTokenWriter.CreateEs256(_key.PrivateKey, _key.KeyId).Write(descriptor);
        }
    }

    public void AddOrReplace(TrustedIssuer issuer)
    {
        ArgumentNullException.ThrowIfNull(issuer);
        lock (_gate)
        {
            _issuers[issuer.Issuer] = issuer;
        }
    }

    public void ReplacePeers(IReadOnlyList<TrustedIssuer> issuers)
    {
        ArgumentNullException.ThrowIfNull(issuers);
        lock (_gate)
        {
            _issuers.Clear();
            _issuers[Application.ToString()] = new TrustedIssuer(
                Application.ToString(),
                _key.PublicJwk);
            for (int index = 0; index < issuers.Count; index++)
            {
                TrustedIssuer issuer = issuers[index]
                    ?? throw new ArgumentException(
                        $"Trusted issuer at index {index} is null.",
                        nameof(issuers));
                if (!string.Equals(issuer.Issuer, Application.ToString(), StringComparison.Ordinal))
                {
                    _issuers[issuer.Issuer] = issuer;
                }
            }
        }
    }

    public IReadOnlyList<TrustedIssuer> Snapshot()
    {
        lock (_gate)
        {
            var names = new List<string>(_issuers.Keys);
            names.Sort(StringComparer.Ordinal);
            var result = new TrustedIssuer[names.Count];
            for (int index = 0; index < result.Length; index++)
            {
                TrustedIssuer issuer = _issuers[names[index]];
                result[index] = new TrustedIssuer(issuer.Issuer, issuer.PublicKey, issuer.AllowedCommandKinds);
            }

            return new ReadOnlyCollection<TrustedIssuer>(result);
        }
    }

    public void ReplaceKey(GatewayTrustKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        lock (_gate)
        {
            GatewayTrustKey previous = _key;
            _key = key;
            _issuers[Application.ToString()] = new TrustedIssuer(
                Application.ToString(),
                key.PublicJwk);
            previous.Dispose();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _key.Dispose();
        }
    }
}
