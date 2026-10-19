namespace Assimalign.Cohesion.Database.Sql.Language;

using Assimalign.Cohesion.Database.Language;

/// <summary>
/// Represents a parsed SQL expression payload.
/// </summary>
public class SqlQueryExpression : QueryExpression
{
    private string? _statementText;

    /// <summary>
    /// Initializes a new <see cref="SqlQueryExpression"/>.
    /// </summary>
    /// <param name="commandType">The inferred top-level command type.</param>
    /// <param name="text">The raw statement text.</param>
    /// <param name="location">The expression location in source text.</param>
    public SqlQueryExpression(SqlQueryCommandType commandType, string? text, Location? location)
        : base(text, location ?? Location.Create(1, 1, 0, 0))
    {
        _statementText = text;
        CommandType = commandType;
    }

    /// <summary>
    /// Gets the raw statement text the expression was parsed from, when available.
    /// </summary>
    public override string? Text => _statementText ?? base.Text;

    /// <summary>
    /// Gets the inferred SQL command type.
    /// </summary>
    public SqlQueryCommandType CommandType { get; }

    /// <summary>
    /// Stamps the raw statement text after parsing (the parser owns the source span;
    /// expression constructors do not).
    /// </summary>
    internal void SetStatementText(string text) => _statementText = text;

    // The nesting the parser measured over the statement this node roots; 0 on any other node.
    private int _measuredNesting;

    /// <summary>
    /// Gets how deep the statement this node roots nests (#1151). On a statement root the parser
    /// returned, it is what the parser measured: the greater of its deepest expression tree and its
    /// deepest grouping parentheses. A query the parser did not return as a root, such as a
    /// subquery taken out of a parsed statement to run on its own, is never parsed again, so its
    /// nesting is the depth of its own expression tree, which every walk over it recurses through.
    /// Zero for a node with no expressions.
    /// </summary>
    /// <remarks>
    /// Taking the greater of the two never lowers a parser's measure, which already includes the
    /// tree. Both values are cached, so reading this is constant-time.
    /// </remarks>
    internal int NestingDepth => System.Math.Max(_measuredNesting, ExpressionTreeDepth);

    /// <summary>
    /// Gets the greatest <see cref="SqlExpression.Depth"/> among the clauses of this query, or 0
    /// when it has none. A query kind that another statement can nest overrides it; every other
    /// kind is only ever a statement root, which the parser measures.
    /// </summary>
    private protected virtual int ExpressionTreeDepth => 0;

    /// <summary>Stamps the nesting the parser measured over the whole statement.</summary>
    internal void SetNestingDepth(int depth) => _measuredNesting = depth;
}
