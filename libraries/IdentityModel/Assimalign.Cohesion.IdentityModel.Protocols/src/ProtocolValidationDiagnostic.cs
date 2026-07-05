using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.IdentityModel.Protocols;

/// <summary>
/// Represents one normalized finding produced by validating a protocol artifact (a
/// message, token, assertion, or metadata document). Diagnostics are the shared currency
/// of validation across the whole IdentityModel family: compliance suites assert on their
/// codes, members, and severities.
/// </summary>
public sealed class ProtocolValidationDiagnostic
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ProtocolValidationDiagnostic" /> class.
    /// </summary>
    /// <param name="severity">How severe the finding is.</param>
    /// <param name="code">The normalized machine-readable finding code (for example <c>issuer_mismatch</c>).</param>
    /// <param name="message">The human-readable finding message.</param>
    /// <param name="member">The field or element of the validated artifact the finding is about, when applicable.</param>
    /// <param name="properties">Additional finding detail. The dictionary is snapshotted; keys compare ordinally.</param>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="code" /> or <paramref name="message" /> is null or
    /// whitespace, or when a property name is blank or a property value is undefined.
    /// </exception>
    public ProtocolValidationDiagnostic(
        ProtocolValidationSeverity severity,
        string code,
        string message,
        string? member = null,
        IReadOnlyDictionary<string, IdentityClaimValue>? properties = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        Severity = severity;
        Code = code;
        Message = message;
        Member = member;
        Properties = ModelSnapshot.Properties(properties, nameof(properties));
    }

    /// <summary>
    /// Gets how severe the finding is.
    /// </summary>
    public ProtocolValidationSeverity Severity { get; }

    /// <summary>
    /// Gets the normalized machine-readable finding code.
    /// </summary>
    public string Code { get; }

    /// <summary>
    /// Gets the human-readable finding message.
    /// </summary>
    public string Message { get; }

    /// <summary>
    /// Gets the field or element of the validated artifact the finding is about, when
    /// applicable.
    /// </summary>
    public string? Member { get; }

    /// <summary>
    /// Gets additional finding detail.
    /// </summary>
    public IReadOnlyDictionary<string, IdentityClaimValue> Properties { get; }

    /// <inheritdoc />
    public override string ToString() => $"[{Severity}] {Code}: {Message}";
}
