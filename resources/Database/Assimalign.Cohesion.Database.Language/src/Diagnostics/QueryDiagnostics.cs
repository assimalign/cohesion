using System;

using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Language;

/// <summary>
/// Creates diagnostics shared by model-specific query languages.
/// </summary>
public static class QueryDiagnostics
{
    /// <summary>Creates the syntax diagnostic for an unsupported parameter spelling.</summary>
    /// <param name="syntaxCode">The language's existing syntax diagnostic code.</param>
    /// <param name="location">The complete unsupported parameter span.</param>
    /// <returns>An error naming the supported parameter forms.</returns>
    /// <exception cref="ArgumentNullException">The syntax code or location is null.</exception>
    public static Diagnostic UnsupportedParameterForm(string syntaxCode, Location location)
    {
        ArgumentNullException.ThrowIfNull(syntaxCode);
        ArgumentNullException.ThrowIfNull(location);
        return new Diagnostic(syntaxCode,
            "Unsupported parameter form; use $name, @name, or $1. Names must be an identifier (a letter or _, then letters, digits or _) or digits only.",
            location.Start, location.End, DiagnosticSeverity.Error)
        {
            Line = location.StartLine,
            Location = DiagnosticLocation.Absolute,
        };
    }

    private const string UnsupportedClauseCode = "COHDBL001";

    /// <summary>
    /// Creates a <c>COHDBL001</c> diagnostic whose message is
    /// "The <c>{clause}</c> clause is not supported by the <c>{language}</c> surface of this
    /// database model."
    /// </summary>
    /// <param name="clause">The clause rejected by the model's language surface.</param>
    /// <param name="language">The language whose profile rejected the clause.</param>
    /// <param name="location">The clause's source location.</param>
    /// <returns>The unsupported-clause error diagnostic.</returns>
    public static Diagnostic UnsupportedClause(string clause, string language, Location location)
    {
        ArgumentNullException.ThrowIfNull(clause);
        ArgumentNullException.ThrowIfNull(language);
        ArgumentNullException.ThrowIfNull(location);

        return new Diagnostic(
            UnsupportedClauseCode,
            $"The {clause} clause is not supported by the {language} surface of this database model.",
            location.Start,
            location.End,
            DiagnosticSeverity.Error)
        {
            Line = location.StartLine,
            Location = DiagnosticLocation.Absolute,
        };
    }
}
