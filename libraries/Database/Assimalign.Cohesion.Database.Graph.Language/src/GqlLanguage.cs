using System;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Language.Gql;

/// <summary>
/// Provides the keyword and function vocabularies for the Graph Query Language (GQL).
/// Aligned with the ISO/IEC 39075 GQL standard.
/// </summary>
public static class GqlLanguage
{
    /// <summary>
    /// GQL reserved keywords.
    /// </summary>
    public static readonly string[] Keywords =
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

    /// <summary>
    /// Common GQL built-in functions.
    /// </summary>
    public static readonly string[] Functions =
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

    /// <summary>
    /// Creates a <see cref="TokenLexerOptions"/> configured for GQL.
    /// </summary>
    public static TokenLexerOptions CreateLexerOptions() => new()
    {
        Keywords = Keywords,
        Functions = Functions,
        IsCaseSensitive = false,
    };
}
