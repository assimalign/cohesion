using System;
using System.Buffers.Text;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace Assimalign.Cohesion.IdentityHub.Hosting.Internal;

internal sealed class DeviceAuthorizationStore
{
    private const string UserCodeAlphabet = "BCDFGHJKLMNPQRSTVWXYZ23456789";
    private readonly Dictionary<string, DeviceAuthorization> _byDeviceCode = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _deviceCodeByUserCode = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    internal DeviceAuthorization Issue(
        IdentityHubClientRegistration client,
        string audience,
        string? scope,
        DateTimeOffset now)
    {
        lock (_gate)
        {
            Prune(now);
            string deviceCode = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
            string userCode;
            do
            {
                userCode = CreateUserCode();
            }
            while (_deviceCodeByUserCode.ContainsKey(userCode));

            var authorization = new DeviceAuthorization(
                deviceCode,
                userCode,
                client,
                audience,
                scope,
                now.AddMinutes(10));
            _byDeviceCode.Add(deviceCode, authorization);
            _deviceCodeByUserCode.Add(userCode, deviceCode);
            return authorization;
        }
    }

    internal bool Approve(string userCode, string subject, DateTimeOffset now)
    {
        lock (_gate)
        {
            Prune(now);
            if (!_deviceCodeByUserCode.TryGetValue(Normalize(userCode), out string? deviceCode) ||
                !_byDeviceCode.TryGetValue(deviceCode, out DeviceAuthorization? authorization))
            {
                return false;
            }

            authorization.Subject = subject;
            return true;
        }
    }

    internal DeviceAuthorizationResult Poll(
        string deviceCode,
        string clientId,
        DateTimeOffset now)
    {
        lock (_gate)
        {
            if (!_byDeviceCode.TryGetValue(deviceCode, out DeviceAuthorization? authorization) ||
                !string.Equals(authorization.Client.ClientId, clientId, StringComparison.Ordinal))
            {
                return new DeviceAuthorizationResult(DeviceAuthorizationStatus.Invalid, null);
            }

            if (authorization.ExpiresAt <= now)
            {
                Remove(authorization);
                return new DeviceAuthorizationResult(DeviceAuthorizationStatus.Expired, null);
            }

            if (authorization.Subject is null)
            {
                return new DeviceAuthorizationResult(DeviceAuthorizationStatus.Pending, null);
            }

            Remove(authorization);
            return new DeviceAuthorizationResult(DeviceAuthorizationStatus.Approved, authorization);
        }
    }

    private static string CreateUserCode()
    {
        Span<byte> bytes = stackalloc byte[8];
        RandomNumberGenerator.Fill(bytes);
        Span<char> characters = stackalloc char[9];
        for (int index = 0; index < 8; index++)
        {
            int target = index < 4 ? index : index + 1;
            characters[target] = UserCodeAlphabet[bytes[index] % UserCodeAlphabet.Length];
        }

        characters[4] = '-';
        return new string(characters);
    }

    private static string Normalize(string userCode) => userCode.Trim().ToUpperInvariant();

    private void Prune(DateTimeOffset now)
    {
        var expired = new List<DeviceAuthorization>();
        foreach (DeviceAuthorization authorization in _byDeviceCode.Values)
        {
            if (authorization.ExpiresAt <= now)
            {
                expired.Add(authorization);
            }
        }

        for (int index = 0; index < expired.Count; index++)
        {
            Remove(expired[index]);
        }
    }

    private void Remove(DeviceAuthorization authorization)
    {
        _byDeviceCode.Remove(authorization.DeviceCode);
        _deviceCodeByUserCode.Remove(authorization.UserCode);
    }
}

internal sealed record DeviceAuthorization(
    string DeviceCode,
    string UserCode,
    IdentityHubClientRegistration Client,
    string Audience,
    string? Scope,
    DateTimeOffset ExpiresAt)
{
    internal string? Subject { get; set; }
}

internal readonly record struct DeviceAuthorizationResult(
    DeviceAuthorizationStatus Status,
    DeviceAuthorization? Authorization);

internal enum DeviceAuthorizationStatus
{
    Invalid,
    Expired,
    Pending,
    Approved,
}
