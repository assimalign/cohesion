using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;

using Assimalign.Cohesion.IdentityHub;

namespace Assimalign.Cohesion.IdentityHub.Hosting;

internal sealed class IdentityHubClientRegistration
{
    private readonly byte[]? _secretHash;

    private IdentityHubClientRegistration(
        string clientId,
        byte[]? secretHash,
        bool allowDeviceAuthorization,
        TimeSpan accessTokenLifetime,
        IReadOnlyList<string> audiences)
    {
        ClientId = clientId;
        _secretHash = secretHash;
        AllowDeviceAuthorization = allowDeviceAuthorization;
        AccessTokenLifetime = accessTokenLifetime;
        Audiences = audiences;
    }

    internal string ClientId { get; }

    internal bool AllowsClientCredentials => _secretHash is not null;

    internal bool AllowDeviceAuthorization { get; }

    internal TimeSpan AccessTokenLifetime { get; }

    internal IReadOnlyList<string> Audiences { get; }

    internal bool VerifySecret(string secret)
    {
        if (_secretHash is null)
        {
            return false;
        }

        byte[] secretBytes = Encoding.UTF8.GetBytes(secret);
        byte[] candidate = SHA256.HashData(secretBytes);
        try
        {
            return CryptographicOperations.FixedTimeEquals(_secretHash, candidate);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secretBytes);
            CryptographicOperations.ZeroMemory(candidate);
        }
    }

    internal bool AllowsAudience(string audience)
    {
        for (int index = 0; index < Audiences.Count; index++)
        {
            if (string.Equals(Audiences[index], audience, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    internal static IdentityHubClientRegistration Create(
        string clientId,
        IdentityHubClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.ClientSecret is { Length: 0 } ||
            (options.ClientSecret is not null && string.IsNullOrWhiteSpace(options.ClientSecret)))
        {
            throw new ArgumentException(
                "An IdentityHub client secret must not be empty or whitespace.",
                nameof(options));
        }

        if (options.AccessTokenLifetime <= TimeSpan.Zero ||
            options.AccessTokenLifetime > TimeSpan.FromHours(24))
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "An IdentityHub access-token lifetime must be greater than zero and no more than 24 hours.");
        }

        var audiences = new List<string>(options.Audiences.Count);
        var unique = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < options.Audiences.Count; index++)
        {
            string audience = options.Audiences[index];
            ArgumentException.ThrowIfNullOrWhiteSpace(audience, nameof(options));
            if (!unique.Add(audience))
            {
                throw new ArgumentException(
                    $"IdentityHub client '{clientId}' contains duplicate audience '{audience}'.",
                    nameof(options));
            }

            audiences.Add(audience);
        }

        byte[]? secretHash = null;
        if (options.ClientSecret is not null)
        {
            byte[] secretBytes = Encoding.UTF8.GetBytes(options.ClientSecret);
            try
            {
                secretHash = SHA256.HashData(secretBytes);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(secretBytes);
            }
        }

        return new IdentityHubClientRegistration(
            clientId,
            secretHash,
            options.AllowDeviceAuthorization,
            options.AccessTokenLifetime,
            new ReadOnlyCollection<string>(audiences));
    }
}
