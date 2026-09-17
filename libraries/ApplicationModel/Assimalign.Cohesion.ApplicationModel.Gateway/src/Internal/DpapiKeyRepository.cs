using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Security.Cryptography;

using Assimalign.Cohesion.Security.DataProtection;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

[SupportedOSPlatform("windows")]
internal sealed class DpapiKeyRepository : IKeyRepository
{
    private readonly IKeyRepository _inner;
    private readonly byte[] _entropy;

    public DpapiKeyRepository(IKeyRepository inner, byte[] entropy)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(entropy);

        _inner = inner;
        _entropy = (byte[])entropy.Clone();
    }

    public IReadOnlyList<KeyDocument> GetAllKeys()
    {
        IReadOnlyList<KeyDocument> protectedKeys = _inner.GetAllKeys();
        var keys = new KeyDocument[protectedKeys.Count];

        for (int index = 0; index < keys.Length; index++)
        {
            KeyDocument key = protectedKeys[index];
            byte[] plaintext = ProtectedData.Unprotect(
                key.Content.Span,
                DataProtectionScope.CurrentUser,
                _entropy);
            keys[index] = new KeyDocument(key.Name, plaintext);
        }

        return keys;
    }

    public void StoreKey(KeyDocument key)
    {
        byte[] protectedContent = ProtectedData.Protect(
            key.Content.Span,
            DataProtectionScope.CurrentUser,
            _entropy);
        _inner.StoreKey(new KeyDocument(key.Name, protectedContent));
    }
}
