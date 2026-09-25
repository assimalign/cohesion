using System;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Internal;

internal interface ILocalFileProtector
{
    byte[] Protect(string resource, string mount, ReadOnlySpan<byte> plaintext);
}
