using System;

// Deviates from the repo namespace-matches-assembly rule per design decision: this
// internal static validator is linked into Protocols and OpenIdConnect, while the
// public ProtocolEndpoint model remains defined only in the Protocols assembly.
namespace Assimalign.Cohesion.IdentityModel.Protocols;

/// <summary>
/// Validates wire-exact endpoint locations for protocol models and metadata projection.
/// </summary>
internal static class EndpointLocation
{
    /// <summary>
    /// Determines whether a string parses as an absolute URI and spells its scheme explicitly.
    /// </summary>
    /// <param name="value">The candidate location.</param>
    /// <returns><see langword="true" /> when the value is a valid endpoint location; otherwise <see langword="false" />.</returns>
    internal static bool IsValid(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return false;
        }

        // System.Uri accepts implicit file paths ("C:\...", "\\server\share"),
        // scheme-relative strings ("//host/path"), and whitespace-padded input as
        // "absolute" — and does so differently per OS ("/path" is an absolute file URI on
        // Unix only). Requiring the wire string to spell its scheme explicitly rejects
        // all of those identically on every platform while still accepting private-use
        // schemes such as "com.example.app:/cb".
        return value.Length > uri.Scheme.Length
            && value.StartsWith(uri.Scheme, StringComparison.OrdinalIgnoreCase)
            && value[uri.Scheme.Length] == ':';
    }
}
