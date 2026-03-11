using System;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Language.Oql;

/// <summary>
/// Provides the keyword and function vocabularies for the Object Query Language (OQL).
/// Based on the ODMG OQL specification.
/// </summary>
public static class OqlLanguage
{
    /// <summary>
    /// OQL reserved keywords.
    /// </summary>
    public static readonly string[] Keywords =
    [
        // Query
        "SELECT", "FROM", "WHERE",
        "ORDER", "BY", "GROUP", "HAVING",
        "DISTINCT", "ALL", "AS",
        // Logical
        "AND", "OR", "NOT",
        // Predicates
        "IN", "EXISTS", "LIKE", "BETWEEN", "IS",
        // Quantifiers
        "FOR", "SOME", "ANY",
        // Literals / Constants
        "NULL", "NIL", "UNDEFINED", "TRUE", "FALSE",
        // Collection constructors
        "STRUCT", "LIST", "SET", "BAG", "ARRAY", "COLLECTION",
        // Object / Collection operations
        "DEFINE", "ELEMENT", "FLATTEN",
        "FIRST", "LAST", "UNIQUE",
        "LISTTOSET",
        // Type
        "TYPEOF",
    ];

    /// <summary>
    /// Common OQL built-in functions.
    /// </summary>
    public static readonly string[] Functions =
    [
        // Aggregate
        "COUNT", "SUM", "AVG", "MIN", "MAX",
        // Numeric
        "ABS",
        // Collection
        "ELEMENT", "FLATTEN", "FIRST", "LAST",
        "LISTTOSET", "UNIQUE",
    ];

    /// <summary>
    /// Creates a <see cref="TokenLexerOptions"/> configured for OQL.
    /// </summary>
    public static TokenLexerOptions CreateLexerOptions() => new()
    {
        Keywords = Keywords,
        Functions = Functions,
        IsCaseSensitive = false,
    };
}
