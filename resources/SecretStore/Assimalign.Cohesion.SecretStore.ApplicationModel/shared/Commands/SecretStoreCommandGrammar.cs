using System;
using System.Globalization;
using System.Net;

// Deviates from the repo RootNamespace rule for shared source: declaration and runtime
// assemblies compile these stateless predicates under their owning namespace.
namespace Assimalign.Cohesion.ApplicationModel.Internal;

internal static class SecretStoreCommandGrammar
{
    internal static bool IsValidSource(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.StartsWith("literal:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        const string parameterPrefix = "parameter:";
        if (value.StartsWith(parameterPrefix, StringComparison.Ordinal))
        {
            return !string.IsNullOrWhiteSpace(value[parameterPrefix.Length..]);
        }

        int separator = value.IndexOf(':');
        if (separator <= 0 || value.IndexOf(':', separator + 1) >= 0 ||
            string.IsNullOrWhiteSpace(value[(separator + 1)..]))
        {
            return false;
        }

        foreach (char character in value.AsSpan(0, separator))
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_' and not '.')
            {
                return false;
            }
        }

        return true;
    }

    internal static bool IsValidPath(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        !string.Equals(value, "trusted-issuers.json", StringComparison.Ordinal) &&
        !value.StartsWith("certs/", StringComparison.Ordinal);

    internal static bool IsValidCertificateName(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value is not "." and not ".." &&
        value.IndexOfAny(['/', '\\']) < 0;

    internal static bool IsValidSubjectAlternativeName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (IPAddress.TryParse(value, out _))
        {
            return true;
        }

        try
        {
            // SubjectAlternativeNameBuilder.AddDnsName uses default IdnMapping.GetAscii
            // before IA5 encoding. Parse that grammar directly; do not construct a certificate.
            // This preserves certificate wildcard/IDN acceptance rather than hostname rules.
            string ascii = new IdnMapping().GetAscii(value);
            return ascii.AsSpan().IndexOfAnyExceptInRange((char)0, (char)127) < 0;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
