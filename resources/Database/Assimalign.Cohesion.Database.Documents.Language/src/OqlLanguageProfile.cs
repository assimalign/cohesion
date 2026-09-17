using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Documents.Language;

/// <summary>
/// Provides the lexical vocabulary and clause capabilities for the Object Query Language (OQL).
/// </summary>
/// <remarks>
/// The query vocabulary is based on the ODMG OQL specification. <c>CREATE INDEX</c> and
/// <c>DROP INDEX</c> are deliberate Cohesion extensions because ODMG specifies no index DDL.
/// </remarks>
public static class OqlLanguageProfile
{
    private static readonly string[] Keywords =
    [
        // Query
        "SELECT", "FROM", "WHERE",
        "ORDER", "BY", "GROUP", "HAVING",
        "DISTINCT", "ALL", "AS", "ASC", "DESC",
        // Index definition
        "CREATE", "DROP", "INDEX", "ON",
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
        OqlClauses.CreateIndex,
        OqlClauses.DropIndex,
        OqlClauses.Select,
        OqlClauses.From,
        OqlClauses.Where,
        OqlClauses.GroupBy,
        OqlClauses.Having,
        OqlClauses.OrderBy,
    ];

    /// <summary>Gets the OQL language profile used by document-model statement consumers.</summary>
    public static QueryLanguageProfile Instance { get; } = new(
        "OQL",
        Keywords,
        Functions,
        Clauses);
}
