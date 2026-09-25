using System;
using System.Security.Cryptography;
using System.Runtime.Versioning;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Internal;

[SupportedOSPlatform("windows")]
internal sealed class WindowsLocalFileProtector : ILocalFileProtector
{
    public WindowsLocalFileProtector(string applicationDirectory, ApplicationName application)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationDirectory);
        _ = application;
    }

    public byte[] Protect(string resource, string mount, ReadOnlySpan<byte> plaintext)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resource);
        ArgumentException.ThrowIfNullOrWhiteSpace(mount);

        byte[] buffer = plaintext.ToArray();
        try
        {
            return ProtectedData.Protect(
                buffer,
                optionalEntropy: null,
                DataProtectionScope.CurrentUser);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }
}
