using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Sql.Language;

using Assimalign.Cohesion.Database.Language;

/// <summary>
/// Represents a <c>CASE</c> expression such as <c>CASE WHEN x = 1 THEN 'a' ELSE 'b' END</c>.
/// </summary>
public sealed class SqlCaseExpression : SqlExpression
{
    /// <summary>
    /// Initializes a new <see cref="SqlCaseExpression"/>.
    /// </summary>
    /// <param name="input">The input expression for a simple CASE, or null for a searched CASE.</param>
    /// <param name="whenClauses">The WHEN/THEN clauses.</param>
    /// <param name="elseResult">The ELSE result, if present.</param>
    /// <param name="location">The source location.</param>
    internal SqlCaseExpression(SqlExpression? input, IReadOnlyList<SqlWhenClause> whenClauses, SqlExpression? elseResult, Location? location)
        : base(location)
    {
        Input = input;
        WhenClauses = whenClauses;
        ElseResult = elseResult;
    }

    /// <summary>
    /// Gets the input expression for a simple CASE, or null for a searched CASE.
    /// </summary>
    public SqlExpression? Input { get; }

    /// <summary>
    /// Gets the WHEN/THEN clauses.
    /// </summary>
    public IReadOnlyList<SqlWhenClause> WhenClauses { get; }

    /// <summary>
    /// Gets the ELSE result expression, if present.
    /// </summary>
    public SqlExpression? ElseResult { get; }
}
