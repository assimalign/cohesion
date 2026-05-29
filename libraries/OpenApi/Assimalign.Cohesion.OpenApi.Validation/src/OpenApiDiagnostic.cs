using System;

namespace Assimalign.Cohesion.OpenApi.Validation;

/// <summary>
/// A single finding produced while validating an OpenAPI document.
/// </summary>
public sealed class OpenApiDiagnostic
{
    /// <summary>Initializes a new instance of the <see cref="OpenApiDiagnostic"/> class.</summary>
    /// <param name="severity">The severity of the finding.</param>
    /// <param name="code">A stable machine-readable code identifying the rule that produced the finding.</param>
    /// <param name="message">A human-readable description of the finding.</param>
    /// <param name="location">A JSON Pointer to the offending location in the document, for example <c>#/info/title</c>.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="code"/>, <paramref name="message"/>, or <paramref name="location"/> is <see langword="null"/>.</exception>
    public OpenApiDiagnostic(OpenApiDiagnosticSeverity severity, string code, string message, string location)
    {
        ArgumentNullException.ThrowIfNull(code);
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(location);

        Severity = severity;
        Code = code;
        Message = message;
        Location = location;
    }

    /// <summary>Gets the severity of the finding.</summary>
    public OpenApiDiagnosticSeverity Severity { get; }

    /// <summary>Gets the stable machine-readable code identifying the rule that produced the finding.</summary>
    public string Code { get; }

    /// <summary>Gets the human-readable description of the finding.</summary>
    public string Message { get; }

    /// <summary>Gets the JSON Pointer to the offending location in the document.</summary>
    public string Location { get; }

    /// <inheritdoc/>
    public override string ToString() => $"[{Severity}] {Code} at {Location}: {Message}";
}
