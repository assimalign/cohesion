namespace Assimalign.Cohesion.Database.Graph.Language.Internal;

/// <summary>
/// The GQL recognized-unsupported table (#1101): every word and label-expression operator the
/// capability scan recognizes but the Graph engine does not run, with the construct its
/// <c>COHDBL001</c> names.
/// </summary>
/// <remarks>
/// Words outside <see cref="GqlLanguageProfile"/>'s keyword list stay identifiers to the
/// lexer; their <see cref="GqlWordPosition"/> says where the scan treats them as a construct.
/// An item that adds an entry adds its case to the keyword-disposition corpus in the same
/// change, or <c>GqlKeywordDispositionTests</c> fails.
/// </remarks>
internal static class GqlUnsupportedVocabulary
{
    /// <summary>The table, in the order the scan documents it.</summary>
    internal static readonly GqlUnsupportedWord[] Words =
    [
        // Clauses, statements and predicates outside the executable subset.
        new("OPTIONAL", GqlClauses.OptionalMatch, GqlWordPosition.Clause),
        new("MANDATORY", GqlClauses.MandatoryMatch, GqlWordPosition.Clause),
        new("ORDER", GqlClauses.OrderBy, GqlWordPosition.Clause),
        new("WITH", "WITH", GqlWordPosition.Clause),
        new("SET", "SET", GqlWordPosition.Clause),
        new("REMOVE", "REMOVE", GqlWordPosition.Clause),
        new("MERGE", "MERGE", GqlWordPosition.Clause),
        new("LIMIT", "LIMIT", GqlWordPosition.Clause),
        new("OFFSET", "OFFSET", GqlWordPosition.Clause),
        new("SKIP", "SKIP", GqlWordPosition.Clause),
        new("LET", "LET", GqlWordPosition.Clause),
        new("UNWIND", "UNWIND", GqlWordPosition.Clause),
        new("FOREACH", "FOREACH", GqlWordPosition.Clause),
        new("UNION", "UNION", GqlWordPosition.Clause),
        new("CASE", "CASE", GqlWordPosition.Clause),
        new("CALL", "CALL", GqlWordPosition.Clause),
        new("YIELD", "YIELD", GqlWordPosition.Clause),
        new("FILTER", "FILTER", GqlWordPosition.Clause),
        new("DISTINCT", "DISTINCT", GqlWordPosition.Clause),
        new("ALL", "ALL", GqlWordPosition.Clause),
        new("OR", "OR", GqlWordPosition.Clause),
        new("NOT", "NOT", GqlWordPosition.Clause),
        new("XOR", "XOR", GqlWordPosition.Clause),
        new("IN", "IN", GqlWordPosition.Clause),
        new("STARTS", "STARTS WITH", GqlWordPosition.Clause),
        new("ENDS", "ENDS WITH", GqlWordPosition.Clause),
        new("CONTAINS", "CONTAINS", GqlWordPosition.Clause),
        new("IS", "IS", GqlWordPosition.Clause),
        new("EXISTS", "EXISTS", GqlWordPosition.Clause),
        new("ANY", "ANY", GqlWordPosition.Clause),
        new("NONE", "NONE", GqlWordPosition.Clause),
        new("SINGLE", "SINGLE", GqlWordPosition.Clause),
        new("LIKE", "LIKE", GqlWordPosition.Clause),
        new("BETWEEN", "BETWEEN", GqlWordPosition.Clause),
        new("SHORTEST", GqlClauses.ShortestPath, GqlWordPosition.Clause),
        // Server, session, catalog and transaction scope.
        new("USE", "USE", GqlWordPosition.Clause),
        new("SESSION", "SESSION", GqlWordPosition.Clause),
        new("SHOW", "SHOW", GqlWordPosition.Clause),
        new("DROP", "DROP", GqlWordPosition.Clause),
        new("ALTER", "ALTER", GqlWordPosition.Clause),
        new("GRANT", "GRANT", GqlWordPosition.Clause),
        new("REVOKE", "REVOKE", GqlWordPosition.Clause),
        new("BEGIN", "BEGIN", GqlWordPosition.Clause),
        new("COMMIT", "COMMIT", GqlWordPosition.Clause),
        new("ROLLBACK", "ROLLBACK", GqlWordPosition.Clause),
        new("FINISH", "FINISH", GqlWordPosition.Clause),
        new("NEXT", "NEXT", GqlWordPosition.Clause),
        new("FOR", "FOR", GqlWordPosition.Clause),
        new("GROUP", "GROUP", GqlWordPosition.Clause),
        new("HAVING", "HAVING", GqlWordPosition.Clause),
        new("DATABASE", "DATABASE", GqlWordPosition.Clause),
        new("GRAPH", "GRAPH", GqlWordPosition.Clause),
        new("SCHEMA", "SCHEMA", GqlWordPosition.Clause),
        new("INDEX", "INDEX", GqlWordPosition.Clause),
        new("CATALOG", "CATALOG", GqlWordPosition.Clause),
        // ISO/IEC 39075 delete and path prefixes. These stay pins with no T1 item lifting them.
        new("NODETACH", "NODETACH DELETE", GqlWordPosition.BeforeDelete),
        new("WALK", "WALK PATH MODE", GqlWordPosition.PathMode),
        new("TRAIL", "TRAIL PATH MODE", GqlWordPosition.PathMode),
        new("SIMPLE", "SIMPLE PATH MODE", GqlWordPosition.PathMode),
        new("ACYCLIC", "ACYCLIC PATH MODE", GqlWordPosition.PathMode),
        // ISO/IEC 39075 label expressions. gql-label-direction flips these pins.
        new("|", "LABEL DISJUNCTION", GqlWordPosition.LabelExpression),
        new("&", "LABEL CONJUNCTION", GqlWordPosition.LabelExpression),
        new("!", "LABEL NEGATION", GqlWordPosition.LabelExpression),
        new("%", "WILDCARD LABEL", GqlWordPosition.LabelExpression),
    ];

    /// <summary>The construct an <c>IS</c> inside a node or edge pattern names: <c>(n IS A)</c>.</summary>
    internal const string IsLabelExpression = "IS LABEL EXPRESSION";

    /// <summary>Finds the entry for an unquoted word or operator.</summary>
    /// <param name="spelling">The word upper-cased with the invariant culture, or the operator.</param>
    /// <param name="word">The entry, when the spelling is in the table.</param>
    /// <returns><see langword="true"/> when the spelling is in the table.</returns>
    internal static bool TryFind(string spelling, out GqlUnsupportedWord word)
    {
        foreach (var candidate in Words)
        {
            if (candidate.Spelling == spelling)
            {
                word = candidate;
                return true;
            }
        }

        word = default;
        return false;
    }
}

/// <summary>One entry of <see cref="GqlUnsupportedVocabulary"/>.</summary>
/// <param name="Spelling">The upper-case word or the operator.</param>
/// <param name="Construct">The construct the diagnostic names.</param>
/// <param name="Position">Where the capability scan treats the spelling as that construct.</param>
internal readonly record struct GqlUnsupportedWord(string Spelling, string Construct, GqlWordPosition Position);

/// <summary>Where the GQL capability scan treats a recognized-unsupported spelling as its construct.</summary>
internal enum GqlWordPosition
{
    /// <summary>
    /// Between clauses, or inside a <c>WHERE</c> or <c>RETURN</c> expression; never as a label,
    /// a property key, a path segment after a dot, or a name inside a pattern. As the first
    /// word of the statement it is the whole statement's construct.
    /// </summary>
    Clause,

    /// <summary>
    /// Directly before <c>DELETE</c>, as in <c>MATCH (n) NODETACH DELETE n</c>. Elsewhere it is a
    /// name.
    /// </summary>
    BeforeDelete,

    /// <summary>
    /// A path mode prefix: between clauses and directly before a path pattern, optionally
    /// followed by <c>PATH</c> or <c>PATHS</c>, as in <c>MATCH TRAIL (a)-[]->(b)</c>. Followed by
    /// <c>=</c> it is a path variable.
    /// </summary>
    PathMode,

    /// <summary>An operator inside a label expression, after <c>:</c> in a node or edge pattern.</summary>
    LabelExpression,
}
