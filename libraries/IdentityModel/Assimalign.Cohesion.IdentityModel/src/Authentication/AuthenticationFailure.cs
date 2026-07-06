using System;

namespace Assimalign.Cohesion.IdentityModel;

/// <summary>
/// Represents why an authentication attempt failed. Failures are values, not exceptions:
/// a failed authentication is a normal, expected outcome that flows through
/// <see cref="AuthenticationResult" />.
/// </summary>
public sealed class AuthenticationFailure
{
    /// <summary>
    /// Initializes a new instance of the <see cref="AuthenticationFailure" /> class.
    /// </summary>
    /// <param name="code">The canonical failure code (see <see cref="AuthenticationFailureCodes" />).</param>
    /// <param name="message">The human-readable failure message.</param>
    /// <param name="description">An optional longer description.</param>
    /// <param name="originalCode">The original wire-level error code before canonical mapping (for example an OAuth <c>error</c> value or a SAML status code).</param>
    /// <param name="errorUri">An optional URI with further error information (for example an OAuth <c>error_uri</c>).</param>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="code" /> or <paramref name="message" /> is null or whitespace.
    /// </exception>
    public AuthenticationFailure(
        string code,
        string message,
        string? description = null,
        string? originalCode = null,
        string? errorUri = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        Code = code;
        Message = message;
        Description = description;
        OriginalCode = originalCode;
        ErrorUri = errorUri;
    }

    /// <summary>
    /// Gets the canonical failure code (see <see cref="AuthenticationFailureCodes" />).
    /// </summary>
    public string Code { get; }

    /// <summary>
    /// Gets the human-readable failure message.
    /// </summary>
    public string Message { get; }

    /// <summary>
    /// Gets the optional longer description.
    /// </summary>
    public string? Description { get; }

    /// <summary>
    /// Gets the original wire-level error code before canonical mapping, preserving
    /// protocol provenance for auditing.
    /// </summary>
    public string? OriginalCode { get; }

    /// <summary>
    /// Gets the optional URI with further error information.
    /// </summary>
    public string? ErrorUri { get; }

    /// <inheritdoc />
    public override string ToString() => $"{Code}: {Message}";
}
