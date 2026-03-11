namespace Assimalign.Cohesion.Database.Language.Sql;

/// <summary>
/// Represents a single WHEN/THEN clause within a CASE expression.
/// </summary>
public sealed class SqlWhenClause
{
    /// <summary>
    /// Initializes a new <see cref="SqlWhenClause"/>.
    /// </summary>
    /// <param name="condition">The WHEN condition expression.</param>
    /// <param name="result">The THEN result expression.</param>
    internal SqlWhenClause(SqlExpression condition, SqlExpression result)
    {
        Condition = condition;
        Result = result;
    }

    /// <summary>
    /// Gets the WHEN condition expression.
    /// </summary>
    public SqlExpression Condition { get; }

    /// <summary>
    /// Gets the THEN result expression.
    /// </summary>
    public SqlExpression Result { get; }
}
