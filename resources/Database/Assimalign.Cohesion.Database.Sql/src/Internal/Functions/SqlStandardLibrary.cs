using System;

using Assimalign.Cohesion.Database.Sql.Language;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// The SQL engine's built-in functions, and the rules every function name follows. The built-ins
/// are internal sealed leaves of the public function family, registered through the same
/// <see cref="SqlFunctionCollection.Add"/> an application calls, so the engine executes its own
/// functions exactly as it executes an application's (phase E2, owner decision 60 of 2026-10-09).
/// </summary>
/// <remarks>
/// <para>
/// Every built-in keeps the permissive behavior it had before typed signatures (owner decision 67):
/// <c>UPPER</c> and <c>LOWER</c> pass a non-string through, <c>LENGTH</c> measures any value's text,
/// <c>ABS</c> widens exact integers to BIGINT and rejects a non-number only when it runs. Tightening
/// them to PostgreSQL's typed overloads is a separate, visible change.
/// </para>
/// <para>
/// The special forms (<c>COALESCE</c>, <c>NULLIF</c>, <c>CASE</c>, <c>CAST</c>, <c>EXTRACT</c>) are
/// not functions: they stay evaluator nodes, as in PostgreSQL's grammar, because a calling
/// convention that evaluates every argument first would change which errors surface (owner decision
/// 66). Only <c>COALESCE</c> executes today.
/// </para>
/// </remarks>
internal static class SqlStandardLibrary
{
    /// <summary>The name of <c>COALESCE</c>, the special form the evaluator computes lazily.</summary>
    internal const string Coalesce = "COALESCE";

    /// <summary>The accepted call forms of <c>COALESCE</c>, as a diagnostic shows them.</summary>
    internal const string CoalesceUsage = "COALESCE(value [, value ...])";

    // The built-ins, stateless and shared by every engine. Registration order is the order a
    // diagnostic lists overloads in: COUNT(*) before COUNT(value), as before.
    private static readonly SqlFunction[] _functions =
    [
        new SqlUpperFunction(),
        new SqlLowerFunction(),
        new SqlLengthFunction(),
        new SqlAbsFunction(),
        new SqlCountFunction(rows: true),
        new SqlCountFunction(rows: false),
        new SqlSumFunction(average: false),
        new SqlSumFunction(average: true),
        new SqlExtremumFunction(maximum: false),
        new SqlExtremumFunction(maximum: true),
    ];

    /// <summary>The special forms: grammar, never catalog functions.</summary>
    private static readonly string[] _specialForms = ["COALESCE", "NULLIF", "CASE", "CAST", "EXTRACT"];

    /// <summary>Registers the built-ins through the public registration path.</summary>
    /// <param name="functions">The collection, empty.</param>
    internal static void Register(SqlFunctionCollection functions)
    {
        foreach (var function in _functions)
        {
            functions.Add(function);
        }
    }

    /// <summary>Whether a function instance is one of the built-ins.</summary>
    /// <param name="function">The function.</param>
    /// <returns><see langword="true"/> for a standard-library leaf.</returns>
    internal static bool Contains(SqlFunction function) => Array.IndexOf(_functions, function) >= 0;

    /// <summary>Whether a call names <c>COALESCE</c>.</summary>
    /// <param name="name">The name as written.</param>
    /// <returns><see langword="true"/> for <c>COALESCE</c>, ignoring case.</returns>
    internal static bool IsCoalesce(string name) => string.Equals(name, Coalesce, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the SQL dialect declares a name as a function that does not execute yet
    /// (<c>NULLIF</c>, <c>TRIM</c>, <c>NOW</c>, ...): a call to it is not an unknown function, and fails
    /// only when it is evaluated, until the dialect rejects such names at parse time (#1103). Read on
    /// the failure path only, for a name the catalog does not hold.
    /// </summary>
    /// <param name="name">The name as written.</param>
    /// <returns><see langword="true"/> when the SQL profile declares the name.</returns>
    internal static bool IsDeclaredName(string name)
    {
        var profile = SqlLanguageProfile.Instance;
        var comparison = profile.IsCaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        foreach (string declared in profile.Functions)
        {
            if (string.Equals(declared, name, comparison))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Checks a function name: an identifier (a letter or underscore, then letters, digits and
    /// underscores, as the lexer reads one) that is neither a special form nor a SQL keyword.
    /// </summary>
    /// <param name="name">The name.</param>
    /// <exception cref="ArgumentException">The name breaks a rule.</exception>
    internal static void ValidateName(string name)
    {
        if (name.Length == 0 || !(char.IsLetter(name[0]) || name[0] == '_'))
        {
            throw new ArgumentException($"Function name '{name}' is not an identifier: it must start with a letter or an underscore.", nameof(name));
        }
        foreach (char character in name)
        {
            if (!(char.IsLetterOrDigit(character) || character == '_'))
            {
                throw new ArgumentException($"Function name '{name}' is not an identifier: it may hold only letters, digits and underscores.", nameof(name));
            }
        }
        foreach (string form in _specialForms)
        {
            if (string.Equals(form, name, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"'{name}' is a SQL special form, not a function, and cannot be registered.", nameof(name));
            }
        }
        foreach (string keyword in SqlLanguageProfile.Instance.Keywords)
        {
            if (string.Equals(keyword, name, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"'{name}' is a SQL keyword and cannot name a function.", nameof(name));
            }
        }
    }
}
