namespace Assimalign.Cohesion.Database.Graph.Language.Internal;

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
    /// <c>CG00002</c>.
    /// </summary>
    EdgePattern,
}
