namespace Assimalign.Cohesion.IdentityModel.Token;

/// <summary>
/// Represents one normalized finding produced by validating an identity token's
/// protocol-neutral data rules. This is the token branch's counterpart to the protocol
/// branch's diagnostic; the branches keep independent copies because the token branch must
/// not reference the protocol branch.
/// </summary>
public sealed class TokenValidationDiagnostic
{
    /// <summary>
    /// Initializes a new instance of the <see cref="TokenValidationDiagnostic" /> class.
    /// </summary>
    /// <param name="severity">How severe the finding is.</param>
    /// <param name="code">The normalized machine-readable finding code (for example <c>issuer_mismatch</c>).</param>
    /// <param name="message">The human-readable finding message.</param>
    /// <param name="member">The token member the finding is about, when applicable.</param>
    /// <exception cref="System.ArgumentException">
    /// Thrown when <paramref name="code" /> or <paramref name="message" /> is null or
    /// whitespace.
    /// </exception>
    public TokenValidationDiagnostic(
        TokenValidationSeverity severity,
        string code,
        string message,
        string? member = null)
    {
        System.ArgumentException.ThrowIfNullOrWhiteSpace(code);
        System.ArgumentException.ThrowIfNullOrWhiteSpace(message);

        Severity = severity;
        Code = code;
        Message = message;
        Member = member;
    }

    /// <summary>
    /// Gets how severe the finding is.
    /// </summary>
    public TokenValidationSeverity Severity { get; }

    /// <summary>
    /// Gets the normalized machine-readable finding code.
    /// </summary>
    public string Code { get; }

    /// <summary>
    /// Gets the human-readable finding message.
    /// </summary>
    public string Message { get; }

    /// <summary>
    /// Gets the token member the finding is about, when applicable.
    /// </summary>
    public string? Member { get; }

    /// <inheritdoc />
    public override string ToString() => $"[{Severity}] {Code}: {Message}";
}
