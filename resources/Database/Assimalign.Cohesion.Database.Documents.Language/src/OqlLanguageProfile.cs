using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Documents.Language;

/// <summary>
/// Provides the lexical vocabulary and clause capabilities for the Object Query Language (OQL).
/// </summary>
/// <remarks>
/// The OQL name is retained for the document model's executable SQL++ profile, following
/// Couchbase SQL++ for Query, including distinct NULL and MISSING values.
/// Collection DDL accepts the SQL++ <c>CREATE COLLECTION [IF NOT EXISTS]</c> and
/// <c>DROP COLLECTION [IF EXISTS]</c> spellings. <c>IF</c> remains positional, not reserved.
/// </remarks>
public static class OqlLanguageProfile
{
    // EXPLAIN is recognized only at the statement prefix; elsewhere it remains an identifier.
    private static readonly string[] _keywords =
    [
        // Query
        "SELECT", "VALUE", "RAW", "FROM", "WHERE",
        "ORDER", "BY", "GROUP", "HAVING",
        "DISTINCT", "ALL", "AS", "ASC", "DESC",
        // Index definition
        "CREATE", "DROP", "INDEX", "ON",
        // Logical
        "AND", "OR", "NOT",
        // Predicates
        "IN", "EXISTS", "LIKE", "ESCAPE", "BETWEEN", "IS", "VALUED", "KNOWN", "UNKNOWN",
        // Conditional expressions
        "CASE", "WHEN", "THEN", "ELSE", "END",
        // Quantifiers
        "FOR", "SOME", "ANY",
        // Literals / Constants
        "NULL", "MISSING", "NIL", "UNDEFINED", "TRUE", "FALSE",
        // Collection constructors
        "SET", "ARRAY", "COLLECTION",
        // Object / Collection operations
        "FIRST", "LAST", "UNIQUE",
    ];

    private static readonly string[] _functions =
    [
        "META",
        // Aggregate
        "COUNT", "SUM", "AVG", "MIN", "MAX",
    ];

    private static readonly string[] _clauses =
    [
        OqlClauses.CreateCollection,
        OqlClauses.DropCollection,
        OqlClauses.CreateIndex,
        OqlClauses.DropIndex,
        OqlClauses.Insert,
        OqlClauses.Values,
        OqlClauses.Select,
        OqlClauses.Distinct,
        OqlClauses.SelectValue,
        OqlClauses.ObjectConstructor,
        OqlClauses.ArrayConstructor,
        OqlClauses.Explain,
        OqlClauses.From,
        OqlClauses.Where,
        OqlClauses.In,
        OqlClauses.Between,
        OqlClauses.Like,
        OqlClauses.Case,
        OqlClauses.GroupBy,
        OqlClauses.Having,
        OqlClauses.OrderBy,
    ];

    /// <summary>Gets the OQL language profile used by document-model statement consumers.</summary>
    public static QueryLanguageProfile Instance { get; } = new(
        "OQL",
        _keywords,
        _functions,
        _clauses);
}
