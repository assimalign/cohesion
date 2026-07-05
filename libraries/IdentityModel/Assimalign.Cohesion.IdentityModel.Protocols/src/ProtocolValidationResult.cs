using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Assimalign.Cohesion.IdentityModel.Protocols;

/// <summary>
/// Represents the immutable outcome of validating a protocol artifact. Validation failures
/// are values, not exceptions: a failed validation is a normal, expected outcome that
/// carries its findings.
/// </summary>
/// <remarks>
/// <see cref="Succeeded" /> is computed — a result succeeds exactly when it carries no
/// <see cref="ProtocolValidationSeverity.Error" /> diagnostics — so a contradictory
/// "succeeded with errors" state is unconstructible. The type is sealed by design:
/// richer validation results elsewhere in the family (for example token validation)
/// compose this type or reuse <see cref="ProtocolValidationDiagnostic" /> directly rather
/// than inheriting. Association with the validated artifact is the caller's composition
/// concern.
/// </remarks>
public sealed class ProtocolValidationResult
{
    private ProtocolValidationResult(
        IReadOnlyList<ProtocolValidationDiagnostic> diagnostics,
        IReadOnlyList<ProtocolValidationDiagnostic> errors)
    {
        Diagnostics = diagnostics;
        Errors = errors;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ProtocolValidationResult" /> class by
    /// snapshotting the provided diagnostics.
    /// </summary>
    /// <param name="diagnostics">The findings, in the order they were produced.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="diagnostics" /> or any entry is null.
    /// </exception>
    public ProtocolValidationResult(IEnumerable<ProtocolValidationDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);

        var snapshot = new List<ProtocolValidationDiagnostic>(
            diagnostics is IReadOnlyCollection<ProtocolValidationDiagnostic> sized ? sized.Count : 4);
        List<ProtocolValidationDiagnostic>? errors = null;

        foreach (var diagnostic in diagnostics)
        {
            ArgumentNullException.ThrowIfNull(diagnostic, nameof(diagnostics));
            snapshot.Add(diagnostic);

            if (diagnostic.Severity == ProtocolValidationSeverity.Error)
            {
                errors ??= new List<ProtocolValidationDiagnostic>();
                errors.Add(diagnostic);
            }
        }

        Diagnostics = new ReadOnlyCollection<ProtocolValidationDiagnostic>(snapshot.ToArray());
        Errors = errors is null
            ? Array.Empty<ProtocolValidationDiagnostic>()
            : new ReadOnlyCollection<ProtocolValidationDiagnostic>(errors.ToArray());
    }

    /// <summary>
    /// Gets the successful validation result with no findings.
    /// </summary>
    public static ProtocolValidationResult Success { get; } =
        new(Array.Empty<ProtocolValidationDiagnostic>(), Array.Empty<ProtocolValidationDiagnostic>());

    /// <summary>
    /// Gets a value indicating whether validation succeeded: no finding has severity
    /// <see cref="ProtocolValidationSeverity.Error" />.
    /// </summary>
    public bool Succeeded => Errors.Count == 0;

    /// <summary>
    /// Gets every finding, in the order it was produced.
    /// </summary>
    public IReadOnlyList<ProtocolValidationDiagnostic> Diagnostics { get; }

    /// <summary>
    /// Gets the error-severity findings, in the order they were produced.
    /// </summary>
    public IReadOnlyList<ProtocolValidationDiagnostic> Errors { get; }

    /// <inheritdoc />
    public override string ToString()
        => Succeeded
            ? $"Succeeded ({Diagnostics.Count} diagnostics)"
            : $"Failed ({Errors.Count} errors, {Diagnostics.Count} diagnostics)";
}
