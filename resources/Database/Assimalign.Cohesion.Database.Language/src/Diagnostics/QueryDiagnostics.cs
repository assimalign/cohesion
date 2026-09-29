using System;

namespace Assimalign.Cohesion.Database.Language;

/// <summary>
/// Creates diagnostics shared by model-specific query languages.
/// </summary>
public static class QueryDiagnostics
{
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
