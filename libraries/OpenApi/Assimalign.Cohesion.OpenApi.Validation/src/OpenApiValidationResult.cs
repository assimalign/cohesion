using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.OpenApi.Validation;

/// <summary>
/// The outcome of validating an OpenAPI document: the full set of diagnostics and convenience views over them.
/// </summary>
public sealed class OpenApiValidationResult
{
    /// <summary>Initializes a new instance of the <see cref="OpenApiValidationResult"/> class.</summary>
    /// <param name="diagnostics">The diagnostics produced during validation.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="diagnostics"/> is <see langword="null"/>.</exception>
    public OpenApiValidationResult(IReadOnlyList<OpenApiDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        Diagnostics = diagnostics;
    }

    /// <summary>Gets all diagnostics produced during validation, in the order they were reported.</summary>
    public IReadOnlyList<OpenApiDiagnostic> Diagnostics { get; }

    /// <summary>Gets a value indicating whether the document produced no <see cref="OpenApiDiagnosticSeverity.Error"/> diagnostics.</summary>
    public bool IsValid => !HasErrors;

    /// <summary>Gets a value indicating whether any <see cref="OpenApiDiagnosticSeverity.Error"/> diagnostics were produced.</summary>
    public bool HasErrors
    {
        get
        {
            foreach (var diagnostic in Diagnostics)
            {
                if (diagnostic.Severity == OpenApiDiagnosticSeverity.Error)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>Gets the diagnostics whose severity is <see cref="OpenApiDiagnosticSeverity.Error"/>.</summary>
    public IEnumerable<OpenApiDiagnostic> Errors => Filter(OpenApiDiagnosticSeverity.Error);

    /// <summary>Gets the diagnostics whose severity is <see cref="OpenApiDiagnosticSeverity.Warning"/>.</summary>
    public IEnumerable<OpenApiDiagnostic> Warnings => Filter(OpenApiDiagnosticSeverity.Warning);

    private IEnumerable<OpenApiDiagnostic> Filter(OpenApiDiagnosticSeverity severity)
    {
        foreach (var diagnostic in Diagnostics)
        {
            if (diagnostic.Severity == severity)
            {
                yield return diagnostic;
            }
        }
    }
}
