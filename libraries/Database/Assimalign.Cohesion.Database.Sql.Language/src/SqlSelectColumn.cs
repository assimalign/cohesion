namespace Assimalign.Cohesion.Database.Language.Sql;

/// <summary>
/// Represents a single item in a SELECT column list, consisting of an expression and optional alias.
/// </summary>
public sealed class SqlSelectColumn
{
    /// <summary>
    /// Initializes a new <see cref="SqlSelectColumn"/>.
    /// </summary>
    /// <param name="expression">The column expression.</param>
    /// <param name="alias">The column alias, if any.</param>
    internal SqlSelectColumn(SqlExpression expression, string? alias)
    {
        Expression = expression;
        Alias = alias;
    }

    /// <summary>
    /// Gets the column expression.
    /// </summary>
    public SqlExpression Expression { get; }

    /// <summary>
    /// Gets the column alias, if present.
    /// </summary>
    public string? Alias { get; }
}
