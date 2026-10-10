namespace Assimalign.Cohesion.Database.Sql.Language;

using Assimalign.Cohesion.Database.Language;

/// <summary>
/// Represents a parsed SQL statement.
/// </summary>
public sealed class SqlQueryStatement : QueryStatement
{
    /// <summary>
    /// Initializes a new <see cref="SqlQueryStatement"/>.
    /// </summary>
    /// <param name="expression">The parsed SQL expression payload.</param>
    public SqlQueryStatement(SqlQueryExpression expression)
    {
        Expression = expression;
    }

    /// <summary>
    /// Gets the parsed expression root.
    /// </summary>
    public override QueryExpression Expression { get; }

    /// <summary>
    /// Gets the typed SQL expression root.
    /// </summary>
    public SqlQueryExpression SqlExpression => (SqlQueryExpression)Expression;

    /// <summary>
    /// Gets how many levels the statement nests, as <see cref="SqlQueryParser"/> measured it while
    /// parsing: the greater of its deepest expression tree, in which a subquery and its clauses
    /// count and an <c>AND</c> or <c>OR</c> chain is one level, and its deepest grouping
    /// parentheses (#1151). A parser with any limit at least this large accepts the statement's
    /// nesting, so an engine whose configured limit is lower refuses it without parsing it again.
    /// </summary>
    /// <remarks>
    /// A statement over a query the parser did not return as a root, such as a subquery taken out
    /// of a parsed statement, measures the depth of that query's expression tree instead: the
    /// query is never parsed again, and the tree is what the engine walks. Zero for a root with no
    /// expressions.
    /// </remarks>
    public int ExpressionNestingDepth => SqlExpression.NestingDepth;
}
