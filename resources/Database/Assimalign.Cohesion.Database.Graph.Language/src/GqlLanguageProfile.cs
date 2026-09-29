using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>
/// Provides the lexical vocabulary and clause capabilities for the Graph Query Language (GQL).
/// </summary>
/// <remarks>The executable ISO/IEC 39075 subset includes documented Cohesion CREATE and SHOW extensions.</remarks>
public static class GqlLanguageProfile
{
    private static readonly string[] _keywords =
    [
        // Pattern matching
        "MATCH", "OPTIONAL", "MANDATORY",
        // Projection
        "RETURN", "WITH", "AS",
        // Mutation
        "CREATE", "INSERT", "DELETE", "DETACH",
        "SET", "REMOVE", "MERGE",
        // Clauses
        "WHERE", "ORDER", "BY", "ASC", "DESC",
        "LIMIT", "OFFSET", "SKIP",
        "LET", "UNWIND", "FOREACH",
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
        "NULL", "TRUE", "FALSE",
        // CASE
        "CASE", "WHEN", "THEN", "ELSE", "END",
        // Graph elements
        "NODE", "RELATIONSHIP", "EDGE",
        "PATH", "SHORTEST", "GRAPH",
        "PROPERTY", "LABEL",
        // Procedures
        "CALL", "YIELD", "FILTER",
        // Cohesion catalog introspection extension
        "SHOW",
    ];

    private static readonly string[] _functions = [];

    private static readonly string[] _clauses =
    [
        GqlClauses.Match,
        GqlClauses.Return,
        GqlClauses.Create,
        GqlClauses.Insert,
        GqlClauses.Delete,
        GqlClauses.DetachDelete,
        GqlClauses.Where,
        GqlClauses.Show,
    ];
    /// <summary>Gets the ISO GQL language profile used by graph-model query consumers.</summary>
    public static QueryLanguageProfile Instance { get; } = new(
        "GQL",
        _keywords,
        _functions,
        _clauses);
}
