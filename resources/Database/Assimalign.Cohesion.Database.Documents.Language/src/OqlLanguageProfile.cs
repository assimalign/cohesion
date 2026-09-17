using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Documents.Language;

/// <summary>
/// Provides the lexical vocabulary and clause capabilities for the Object Query Language (OQL).
/// </summary>
/// <remarks>The vocabulary is based on the ODMG OQL specification.</remarks>
public static class OqlLanguageProfile
{
    private static readonly string[] Keywords =
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

    private static readonly string[] Functions =
    [
        // Aggregate
        "COUNT", "SUM", "AVG", "MIN", "MAX",
        // Numeric
        "ABS",
        // Collection
        "ELEMENT", "FLATTEN", "FIRST", "LAST",
        "LISTTOSET", "UNIQUE",
    ];

    private static readonly string[] Clauses =
    [
        OqlClauses.Select,
        OqlClauses.From,
        OqlClauses.Where,
        OqlClauses.GroupBy,
        OqlClauses.Having,
        OqlClauses.OrderBy,
        OqlClauses.Define,
        OqlClauses.Element,
        OqlClauses.Flatten,
        OqlClauses.Subquery,
    ];

    /// <summary>Gets the OQL language profile used by document-model query consumers.</summary>
    public static QueryLanguageProfile Instance { get; } = new(
        "OQL",
        Keywords,
        Functions,
        Clauses);
}
