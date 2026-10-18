namespace Assimalign.Cohesion.Database.Graph.Language.Internal;

/// <summary>
/// The GQL recognized-unsupported table (#1101): every word and edge spelling the capability
/// scan recognizes but the Graph engine does not run, with the construct its <c>COHDBL001</c>
/// names.
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
        new("MANDATORY", GqlClauses.MandatoryMatch, GqlWordPosition.Clause),
        new("SET", "SET", GqlWordPosition.Clause),
        new("REMOVE", "REMOVE", GqlWordPosition.Clause),
        new("MERGE", "MERGE", GqlWordPosition.Clause),
        new("FOREACH", "FOREACH", GqlWordPosition.Clause),
        new("UNION", "UNION", GqlWordPosition.Clause),
        new("CALL", "CALL", GqlWordPosition.Clause),
        new("YIELD", "YIELD", GqlWordPosition.Clause),
        new("ALL", "ALL", GqlWordPosition.Clause),
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
        new("HAVING", "HAVING", GqlWordPosition.Clause),
        // Function-only words remain legal variables and property names.
        new("ID", "FUNCTION id (use element_id)", GqlWordPosition.FunctionCall),
        new("REDUCE", "FUNCTION reduce", GqlWordPosition.FunctionCall),
        new("DATE", "FUNCTION date", GqlWordPosition.FunctionCall),
        new("DATETIME", "FUNCTION datetime", GqlWordPosition.FunctionCall),
        new("DURATION", "FUNCTION duration", GqlWordPosition.FunctionCall),
        new("TIME", "FUNCTION time", GqlWordPosition.FunctionCall),
        new("LOCALTIME", "FUNCTION localtime", GqlWordPosition.FunctionCall),
        new("ZONED_DATETIME", "FUNCTION zoned_datetime", GqlWordPosition.FunctionCall),
        new("ZONED_TIME", "FUNCTION zoned_time", GqlWordPosition.FunctionCall),
        new("LOCAL_DATETIME", "FUNCTION local_datetime", GqlWordPosition.FunctionCall),
        new("LOCALDATETIME", "FUNCTION localdatetime", GqlWordPosition.FunctionCall),
        new("HEAD", "FUNCTION head", GqlWordPosition.FunctionCall),
        new("LAST", "FUNCTION last", GqlWordPosition.FunctionCall),
        new("TAIL", "FUNCTION tail", GqlWordPosition.FunctionCall),
        new("REVERSE", "FUNCTION reverse", GqlWordPosition.FunctionCall),
        new("RANGE", "FUNCTION range", GqlWordPosition.FunctionCall),
        new("ELEMENTS", "FUNCTION elements", GqlWordPosition.FunctionCall),
        new("CURRENT_TIME", "CURRENT_TIME", GqlWordPosition.ValueExpression),
        new("LOCAL_TIME", "LOCAL_TIME", GqlWordPosition.ValueExpression),
        new("LOCAL_TIMESTAMP", "LOCAL_TIMESTAMP", GqlWordPosition.ValueExpression),
        // ISO statistical aggregates are recognized for a later tier.
        new("STDDEV_SAMP", "STDDEV_SAMP", GqlWordPosition.FunctionCall),
        new("STDDEV_POP", "STDDEV_POP", GqlWordPosition.FunctionCall),
        new("PERCENTILE_CONT", "PERCENTILE_CONT", GqlWordPosition.FunctionCall),
        new("PERCENTILE_DISC", "PERCENTILE_DISC", GqlWordPosition.FunctionCall),
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
        // ISO/IEC 39075 undirected (tilde) edges: the engine stores only directed relationships
        // (#1139). The label-expression operators #1101 listed here are executable since #1139;
        // GqlLabelVocabulary lists them.
        new("~", UndirectedEdge, GqlWordPosition.EdgePattern),
    ];

    /// <summary>
    /// The construct a tilde edge names: <c>~[]~</c>, <c>&lt;~[]~</c>, <c>~[]~&gt;</c>, <c>~</c>,
    /// <c>&lt;~</c> or <c>~&gt;</c> directly after a node pattern.
    /// </summary>
    internal const string UndirectedEdge = "UNDIRECTED EDGE";

    /// <summary>The ISO OPTIONAL brace and parenthesized operands, outside the T1 subset.</summary>
    internal const string OptionalBlockOperand = "OPTIONAL BLOCK OPERAND";

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

    /// <summary>Only an invocation in a value expression, followed by an opening parenthesis.</summary>
    FunctionCall,

    /// <summary>A reserved niladic datetime expression in a value position.</summary>
    ValueExpression,

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

    /// <summary>
    /// An edge's opening spelling directly after a node pattern's <c>)</c>: <c>~</c>, or
    /// <c>&lt;~</c> written without a space. Elsewhere the character is not GQL and fails with
    /// <c>GQL0002</c>.
    /// </summary>
    EdgePattern,
}
