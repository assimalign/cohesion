using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Language;

/// <summary>
/// The grammar surface one model's query language exposes: its lexical vocabulary and the set of
/// clauses its parser accepts. A clause outside the profile is reported as unsupported by that
/// model rather than failing as a generic syntax error.
/// </summary>
public sealed class QueryLanguageProfile
{
    private readonly string[] _keywords;
    private readonly string[] _functions;
    private readonly IReadOnlyCollection<string> _clauses;
    private readonly HashSet<string> _supportedClauses;

    /// <summary>
    /// Initializes a query language profile.
    /// </summary>
    /// <param name="language">The name of the query language.</param>
    /// <param name="keywords">The reserved keywords recognized by the lexer.</param>
    /// <param name="functions">The builtin function names recognized by the lexer.</param>
    /// <param name="clauses">The clauses accepted by the model's parser.</param>
    /// <param name="isCaseSensitive">
    /// Whether keyword, function, and clause matching is case-sensitive.
    /// </param>
    public QueryLanguageProfile(
        string language,
        string[] keywords,
        string[] functions,
        string[] clauses,
        bool isCaseSensitive = false)
    {
        ArgumentNullException.ThrowIfNull(language);
        ArgumentNullException.ThrowIfNull(keywords);
        ArgumentNullException.ThrowIfNull(functions);
        ArgumentNullException.ThrowIfNull(clauses);

        Language = language;
        IsCaseSensitive = isCaseSensitive;

        _keywords = (string[])keywords.Clone();
        _functions = (string[])functions.Clone();

        var clauseValues = (string[])clauses.Clone();
        _clauses = Array.AsReadOnly(clauseValues);
        _supportedClauses = new HashSet<string>(
            clauseValues,
            isCaseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The language this profile describes — "SQL", "OQL", or "GQL".</summary>
    public string Language { get; }

    /// <summary>Reserved keywords the lexer recognizes.</summary>
    public ReadOnlySpan<string> Keywords => _keywords;

    /// <summary>Builtin function names the lexer recognizes.</summary>
    public ReadOnlySpan<string> Functions => _functions;

    /// <summary>The clauses this model's parser accepts.</summary>
    public IReadOnlyCollection<string> Clauses => _clauses;

    /// <summary>Whether keyword, function, and clause matching is case-sensitive.</summary>
    public bool IsCaseSensitive { get; }

    /// <summary>Whether <paramref name="clause"/> is accepted by this model.</summary>
    /// <param name="clause">The language-specific clause name to inspect.</param>
    /// <returns><see langword="true"/> when the profile accepts the clause.</returns>
    public bool Supports(string clause)
    {
        ArgumentNullException.ThrowIfNull(clause);
        return _supportedClauses.Contains(clause);
    }

    /// <summary>Projects this profile onto the lexer's options.</summary>
    /// <returns>Lexer options backed by this profile's immutable lexical tables.</returns>
    public TokenLexerOptions ToLexerOptions() => new()
    {
        Keywords = _keywords,
        Functions = _functions,
        IsCaseSensitive = IsCaseSensitive,
    };
}
