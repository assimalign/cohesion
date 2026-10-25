namespace Assimalign.Cohesion.Database.Sql.Language;

using Assimalign.Cohesion.Database.Language;

/// <summary>
/// Represents a parenthesized subquery used in a scalar position, such as <c>(SELECT MAX(Id) FROM Users)</c>.
/// </summary>
[global::System.Diagnostics.DebuggerDisplay("SqlSubqueryExpression")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Language.SqlSubqueryExpression.DebugView))]
public sealed class SqlSubqueryExpression : SqlExpression
{
    /// <summary>
    /// Initializes a new <see cref="SqlSubqueryExpression"/>.
    /// </summary>
    /// <param name="select">The query body forming the subquery.</param>
    /// <param name="location">The source location.</param>
    internal SqlSubqueryExpression(SqlQueryBodyExpression select, Location? location)
        : base(location)
    {
        Select = select;
        Depth = 1 + select.ExpressionDepth;
    }

    /// <summary>
    /// Gets the SELECT or set-operation body forming the subquery.
    /// </summary>
    public SqlQueryBodyExpression Select { get; }

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Sql.Language.SqlSubqueryExpression _target;

        public DebugView(global::Assimalign.Cohesion.Database.Sql.Language.SqlSubqueryExpression value)
        {
            _target = value;
        }

        public global::Assimalign.Cohesion.Database.Sql.Language.SqlQueryBodyExpression Select => _target.Select;
    }
}
