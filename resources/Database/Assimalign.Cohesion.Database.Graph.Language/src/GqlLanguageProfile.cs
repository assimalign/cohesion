using Assimalign.Cohesion.Database.Graph.Language.Internal;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>
/// Provides the lexical vocabulary and clause capabilities for the Graph Query Language (GQL).
/// </summary>
/// <remarks>The executable ISO/IEC 39075 subset includes documented Cohesion CREATE and SHOW extensions.</remarks>
public static class GqlLanguageProfile
{
    // EXPLAIN is recognized only at the statement prefix; elsewhere it remains an identifier.
    private static readonly string[] _keywords =
    [
        // Pattern matching
        "MATCH", "OPTIONAL", "MANDATORY",
        // Projection
        "RETURN", "NEXT", "WITH", "AS",
        // Mutation
        "CREATE", "INSERT", "DELETE", "DETACH",
        "SET", "REMOVE", "MERGE",
        // Clauses
        "WHERE", "GROUP", "ORDER", "BY", "ASC", "ASCENDING", "DESC", "DESCENDING",
        "LIMIT", "OFFSET", "SKIP",
        "LET", "FOR", "UNWIND", "FOREACH",
        // Set operations
        "UNION", "DISTINCT", "ALL",
        // Logical
        "AND", "OR", "NOT", "XOR",
        // Predicates
        "IN", "STARTS", "ENDS", "CONTAINS",
        "IS", "EXISTS",
        // Quantifiers
        "ANY", "NONE", "SINGLE",
        // Literals / Constants
        "NULL", "TRUE", "FALSE", "UNKNOWN",
        // CASE
        "CASE", "WHEN", "THEN", "ELSE", "END", "COALESCE", "NULLIF",
        // Graph elements
        "NODE", "RELATIONSHIP", "EDGE",
        "PATH", "SHORTEST", "GRAPH",
        "PROPERTY", "LABEL",
        // Procedures
        "CALL", "YIELD", "FILTER",
        // Cohesion catalog introspection extension
        "SHOW",
    ];

    private static readonly string[] _functions =
    [
        .. GqlScalarFunctions.Signatures.Keys, "cast",
        "count", "sum", "avg", "min", "max", "collect_list", "collect",
    ];

    private static readonly string[] _clauses =
    [
        GqlClauses.Match,
        GqlClauses.QuantifiedPath,
        GqlClauses.OptionalMatch,
        GqlClauses.Explain,
        GqlClauses.Return,
        GqlClauses.Next,
        GqlClauses.With,
        GqlClauses.GroupBy,
        GqlClauses.OrderBy,
        GqlClauses.Offset,
        GqlClauses.Skip,
        GqlClauses.Limit,
        GqlClauses.Case,
        GqlClauses.Create,
        GqlClauses.Insert,
        GqlClauses.Delete,
        GqlClauses.DetachDelete,
        GqlClauses.Where,
        GqlClauses.Filter,
        GqlClauses.Let,
        GqlClauses.For,
        GqlClauses.Unwind,
        GqlClauses.Show,
        GqlClauses.LabelExpression,
    ];
    /// <summary>Gets the ISO GQL language profile used by graph-model query consumers.</summary>
    public static QueryLanguageProfile Instance { get; } = new(
        "GQL",
        _keywords,
        _functions,
        _clauses);
}
