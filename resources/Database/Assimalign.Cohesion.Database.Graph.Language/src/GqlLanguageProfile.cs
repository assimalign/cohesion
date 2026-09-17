using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>
/// Provides the lexical vocabulary and clause capabilities for the Graph Query Language (GQL).
/// </summary>
/// <remarks>The vocabulary remains aligned with the ISO/IEC 39075 GQL standard.</remarks>
public static class GqlLanguageProfile
{
    private static readonly string[] Keywords =
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
    ];

    private static readonly string[] Functions =
    [
        // Aggregate
        "count", "sum", "avg", "min", "max", "collect",
        // Scalar
        "size", "length", "type", "id", "elementId",
        "labels", "properties", "keys",
        // Path / Relationship
        "nodes", "relationships",
        "startNode", "endNode",
        // List
        "head", "tail", "last", "range",
        // Math
        "abs", "ceil", "floor", "round", "sign",
        "rand", "sqrt", "log", "exp",
        // Conversion
        "toInteger", "toFloat", "toString",
        // Null
        "coalesce",
        // Temporal
        "timestamp", "date", "time", "datetime", "duration",
        // Spatial
        "point", "distance",
    ];

    private static readonly string[] Clauses =
    [
        GqlClauses.Match,
        GqlClauses.OptionalMatch,
        GqlClauses.MandatoryMatch,
        GqlClauses.Return,
        GqlClauses.With,
        GqlClauses.Create,
        GqlClauses.Insert,
        GqlClauses.Delete,
        GqlClauses.DetachDelete,
        GqlClauses.Set,
        GqlClauses.Remove,
        GqlClauses.Merge,
        GqlClauses.Where,
        GqlClauses.OrderBy,
        GqlClauses.Limit,
        GqlClauses.Offset,
        GqlClauses.Skip,
        GqlClauses.Let,
        GqlClauses.Unwind,
        GqlClauses.Foreach,
        GqlClauses.SetOperation,
        GqlClauses.Case,
        GqlClauses.Call,
        GqlClauses.Yield,
        GqlClauses.Filter,
        GqlClauses.ShortestPath,
    ];

    /// <summary>Gets the ISO GQL language profile used by graph-model query consumers.</summary>
    public static QueryLanguageProfile Instance { get; } = new(
        "GQL",
        Keywords,
        Functions,
        Clauses);
}
