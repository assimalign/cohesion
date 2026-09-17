using System;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

internal interface ILocalFileProtector
{
    byte[] Protect(string resource, string mount, ReadOnlySpan<byte> plaintext);
}
