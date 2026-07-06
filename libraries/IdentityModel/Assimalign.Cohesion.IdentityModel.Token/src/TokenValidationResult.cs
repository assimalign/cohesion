using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Assimalign.Cohesion.IdentityModel.Token;

/// <summary>
/// Represents the immutable outcome of validating an identity token's protocol-neutral data
/// rules. Validation failures are values, not exceptions: a failed validation is a normal,
/// expected outcome that carries its findings.
/// </summary>
/// <remarks>
/// This mirrors the protocol branch's <c>ProtocolValidationResult</c> contract — computed
/// <see cref="Succeeded" /> so a "succeeded with errors" state is unconstructible, snapshotted
/// diagnostics, sealed — but is an independent branch-local type, because the token branch is
/// forbidden from referencing the protocol branch (a family boundary rule, enforced by the
/// architecture tests). The two branches keep parallel copies rather than sharing the
/// protocol validation currency.
/// </remarks>
public sealed class TokenValidationResult
{
    private TokenValidationResult(
        IReadOnlyList<TokenValidationDiagnostic> diagnostics,
        IReadOnlyList<TokenValidationDiagnostic> errors)
    {
        Diagnostics = diagnostics;
        Errors = errors;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="TokenValidationResult" /> class by
    /// snapshotting the provided diagnostics.
    /// </summary>
    /// <param name="diagnostics">The findings, in the order they were produced.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="diagnostics" /> or any entry is null.
    /// </exception>
    public TokenValidationResult(IEnumerable<TokenValidationDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);

        var snapshot = new List<TokenValidationDiagnostic>(
            diagnostics is IReadOnlyCollection<TokenValidationDiagnostic> sized ? sized.Count : 4);
        List<TokenValidationDiagnostic>? errors = null;

        foreach (var diagnostic in diagnostics)
        {
            ArgumentNullException.ThrowIfNull(diagnostic, nameof(diagnostics));
            snapshot.Add(diagnostic);

            if (diagnostic.Severity == TokenValidationSeverity.Error)
            {
                errors ??= new List<TokenValidationDiagnostic>();
                errors.Add(diagnostic);
            }
        }

        Diagnostics = new ReadOnlyCollection<TokenValidationDiagnostic>(snapshot.ToArray());
        Errors = errors is null
            ? Array.Empty<TokenValidationDiagnostic>()
            : new ReadOnlyCollection<TokenValidationDiagnostic>(errors.ToArray());
    }

    /// <summary>
    /// Gets the successful validation result with no findings.
    /// </summary>
    public static TokenValidationResult Success { get; } =
        new(Array.Empty<TokenValidationDiagnostic>(), Array.Empty<TokenValidationDiagnostic>());

    /// <summary>
    /// Gets a value indicating whether validation succeeded: no finding has severity
    /// <see cref="TokenValidationSeverity.Error" />.
    /// </summary>
    public bool Succeeded => Errors.Count == 0;

    /// <summary>
    /// Gets every finding, in the order it was produced.
    /// </summary>
    public IReadOnlyList<TokenValidationDiagnostic> Diagnostics { get; }

    /// <summary>
    /// Gets the error-severity findings, in the order they were produced.
    /// </summary>
    public IReadOnlyList<TokenValidationDiagnostic> Errors { get; }

    /// <inheritdoc />
    public override string ToString()
        => Succeeded
            ? $"Succeeded ({Diagnostics.Count} diagnostics)"
            : $"Failed ({Errors.Count} errors, {Diagnostics.Count} diagnostics)";
}
