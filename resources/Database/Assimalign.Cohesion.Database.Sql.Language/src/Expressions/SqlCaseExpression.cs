using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Sql.Language;

using Assimalign.Cohesion.Database.Language;

/// <summary>
/// Represents a <c>CASE</c> expression such as <c>CASE WHEN x = 1 THEN 'a' ELSE 'b' END</c>.
/// </summary>
[global::System.Diagnostics.DebuggerDisplay("SqlCaseExpression")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Language.SqlCaseExpression.DebugView))]
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

        int depth = System.Math.Max(DepthOf(input), DepthOf(elseResult));
        foreach (var clause in whenClauses)
        {
            depth = System.Math.Max(depth, System.Math.Max(clause.Condition.Depth, clause.Result.Depth));
        }

        Depth = 1 + depth;
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

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Sql.Language.SqlCaseExpression _target;

        public DebugView(global::Assimalign.Cohesion.Database.Sql.Language.SqlCaseExpression value)
        {
            _target = value;
        }

        public global::Assimalign.Cohesion.Database.Sql.Language.SqlExpression? Input => _target.Input;
        public global::System.Collections.Generic.IReadOnlyList<global::Assimalign.Cohesion.Database.Sql.Language.SqlWhenClause> WhenClauses => _target.WhenClauses;
        public global::Assimalign.Cohesion.Database.Sql.Language.SqlExpression? ElseResult => _target.ElseResult;
    }
}
