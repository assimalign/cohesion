using System;

// Deviates from the repo RootNamespace rule for shared source: linked copies retain their owning namespace.
namespace Assimalign.Cohesion.SecretStore.Client.Internal;

internal static class SecretStoreCertificateReferenceGrammar
{
    internal static bool IsValid(string? value)
    {
        if (value == "ca/root")
        {
            return true;
        }

        if (value is null || !value.StartsWith("certs/", StringComparison.Ordinal))
        {
            return false;
        }

        ReadOnlySpan<char> leaf = value.AsSpan("certs/".Length);
        return !leaf.IsWhiteSpace()
            && !leaf.SequenceEqual(".")
            && !leaf.SequenceEqual("..")
            && leaf.IndexOfAny('/', '\\') < 0;
    }
}
