using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;

using Assimalign.Cohesion.IdentityModel;
using Assimalign.Cohesion.IdentityModel.Token.JsonWebToken;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

internal sealed class ApplicationTrustState : IDisposable
{
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

    public string Issue(string audience, string subject, TimeSpan lifetime, DateTimeOffset now)
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
                result[index] = new TrustedIssuer(issuer.Issuer, issuer.PublicKey);
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
