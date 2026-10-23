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
            ErrorMessages.UnsupportedParameterFormDiagnostic,
            location.Start, location.End, DiagnosticSeverity.Error)
        {
            Line = location.StartLine,
            Location = DiagnosticLocation.Absolute,
        };
    }

    private const string unsupportedClauseCode = "CK00001";

    /// <summary>
    /// Creates a <c>CK00001</c> diagnostic whose message is
    /// "The <c>{clause}</c> clause is not supported by the <c>{language}</c> surface of this
    /// database model."
    /// </summary>
    /// <param name="clause">The clause rejected by the model's language surface.</param>
    /// <param name="language">The language whose profile rejected the clause.</param>
    /// <param name="location">The clause's source location.</param>
    /// <returns>The unsupported-clause error diagnostic.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    public static Diagnostic UnsupportedClause(string clause, string language, Location location)
    {
        ArgumentNullException.ThrowIfNull(clause);
        ArgumentNullException.ThrowIfNull(language);
        ArgumentNullException.ThrowIfNull(location);

        return new Diagnostic(
            unsupportedClauseCode,
            string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.UnsupportedClauseDiagnostic, clause, language),
            location.Start,
            location.End,
            DiagnosticSeverity.Error)
        {
            Line = location.StartLine,
            Location = DiagnosticLocation.Absolute,
        };
    }
}
