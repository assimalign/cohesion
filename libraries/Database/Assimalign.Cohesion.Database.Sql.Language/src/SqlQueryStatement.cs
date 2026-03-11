namespace Assimalign.Cohesion.Database.Language.Sql;

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
}
