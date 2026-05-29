using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.OpenApi.Validation;

/// <summary>
/// The mutable state passed to each <see cref="IOpenApiValidationRule"/> during a validation pass.
/// Rules read <see cref="Document"/> and report findings through the <c>Report</c> helpers.
/// </summary>
public sealed class OpenApiValidationContext
{
    private readonly List<OpenApiDiagnostic> _diagnostics = new();

    /// <summary>Initializes a new instance of the <see cref="OpenApiValidationContext"/> class.</summary>
    /// <param name="document">The document under validation.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="document"/> is <see langword="null"/>.</exception>
    public OpenApiValidationContext(OpenApiDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        Document = document;
    }

    /// <summary>Gets the document under validation.</summary>
    public OpenApiDocument Document { get; }

    /// <summary>Gets the diagnostics reported so far.</summary>
    public IReadOnlyList<OpenApiDiagnostic> Diagnostics => _diagnostics;

    /// <summary>Reports a diagnostic of the given severity.</summary>
    /// <param name="severity">The severity of the finding.</param>
    /// <param name="code">The rule code.</param>
    /// <param name="message">The human-readable message.</param>
    /// <param name="location">The JSON Pointer to the offending location.</param>
    public void Report(OpenApiDiagnosticSeverity severity, string code, string message, string location) =>
        _diagnostics.Add(new OpenApiDiagnostic(severity, code, message, location));

    /// <summary>Reports an <see cref="OpenApiDiagnosticSeverity.Error"/> diagnostic.</summary>
    /// <param name="code">The rule code.</param>
    /// <param name="message">The human-readable message.</param>
    /// <param name="location">The JSON Pointer to the offending location.</param>
    public void Error(string code, string message, string location) =>
        Report(OpenApiDiagnosticSeverity.Error, code, message, location);

    /// <summary>Reports a <see cref="OpenApiDiagnosticSeverity.Warning"/> diagnostic.</summary>
    /// <param name="code">The rule code.</param>
    /// <param name="message">The human-readable message.</param>
    /// <param name="location">The JSON Pointer to the offending location.</param>
    public void Warning(string code, string message, string location) =>
        Report(OpenApiDiagnosticSeverity.Warning, code, message, location);
}
