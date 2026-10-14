using System;
using System.Diagnostics;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// An opaque, nonblank application credential value containing no carriage return or line feed.
/// </summary>
/// <remarks>
/// Credential contents compare ordinally and retain their original spelling. The value is redacted
/// in string and debugger displays; transports must explicitly read <see cref="Value"/>.
/// A default value has no credential and is refused by <see cref="ApplicationCredential"/>.
/// </remarks>
[DebuggerDisplay("<redacted>")]
public partial struct ApplicationCredentialValue
{
    /// <summary>
    /// Determines whether a credential is nonblank and contains neither carriage return nor line feed.
    /// </summary>
    /// <param name="value">The candidate credential.</param>
    /// <param name="message">The reason for rejection, excluding the credential, or null when valid.</param>
    /// <returns>True when the credential is nonblank and occupies a single line.</returns>
    public static partial bool IsValid(string value, out string? message)
    {
        if (string.IsNullOrWhiteSpace(value) || value.AsSpan().IndexOfAny('\r', '\n') >= 0)
        {
            message = ErrorMessages.ApplicationCredentialValueInvalid;
            return false;
        }

        message = null;
        return true;
    }

    /// <summary>
    /// Returns a fixed redaction marker without revealing the credential.
    /// </summary>
    /// <returns>The text <c>&lt;redacted&gt;</c>, including for a default value.</returns>
    public override string ToString() => "<redacted>";

    /// <summary>
    /// Validates a string as an application credential value.
    /// </summary>
    /// <param name="value">The credential to validate.</param>
    /// <returns>The validated credential value.</returns>
    /// <exception cref="ArgumentException">The credential is blank or contains a carriage return or line feed.</exception>
    /// <remarks>No reverse string conversion is supplied; read <see cref="Value"/> explicitly for transport.</remarks>
    public static implicit operator ApplicationCredentialValue(string value) => new(value);
}
