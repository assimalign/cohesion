using Assimalign.Cohesion.Http.Internal;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// A non-empty RFC 6265 &#167; 4.1.1 <c>extension-av</c>: US-ASCII characters
/// from <c>0x20</c> through <c>0x7E</c>, excluding <c>;</c>.
/// </summary>
/// <remarks>
/// Construction and implicit conversion from <see cref="string"/> refuse
/// null, empty, control, non-ASCII, or semicolon-containing input with
/// <see cref="System.ArgumentException"/>. Each value represents one attribute,
/// so untrusted text cannot inject another attribute or a response field.
/// <see langword="default"/> holds no extension and is refused by
/// <see cref="HttpCookieOptions.Extensions"/>.
/// </remarks>
public readonly partial struct HttpCookieExtension
{
    /// <summary>
    /// Determines whether a candidate is a non-empty RFC 6265 <c>extension-av</c>.
    /// </summary>
    /// <param name="value">The candidate extension attribute.</param>
    /// <param name="message">The refusal reason, or <see langword="null"/> when valid.</param>
    /// <returns><see langword="true"/> for non-empty US-ASCII without controls or <c>;</c>.</returns>
    public static partial bool IsValid(string value, out string? message)
    {
        // extension-av has the path-value character set, but requires a value here.
        // Do not echo input: rejected control characters must not reach diagnostics.
        if (string.IsNullOrEmpty(value) || !HttpCookieGrammar.IsValidPath(value))
        {
            message = ErrorMessages.CookieExtensionInvalid;
            return false;
        }

        message = null;
        return true;
    }
}
