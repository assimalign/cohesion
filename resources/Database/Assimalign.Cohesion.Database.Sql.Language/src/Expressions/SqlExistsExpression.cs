namespace Assimalign.Cohesion.Database.Sql.Language;

using Assimalign.Cohesion.Database.Language;

/// <summary>
/// Represents an <c>EXISTS</c> predicate such as <c>EXISTS (SELECT ...)</c>.
/// </summary>
[global::System.Diagnostics.DebuggerDisplay("SqlExistsExpression")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Language.SqlExistsExpression.DebugView))]
public sealed class SqlExistsExpression : SqlExpression
{
    /// <summary>
    /// Initializes a new <see cref="SqlExistsExpression"/>.
    /// </summary>
    /// <param name="subquery">The subquery being tested.</param>
    /// <param name="isNegated">Whether the predicate is negated (<c>NOT EXISTS</c>).</param>
    /// <param name="location">The source location.</param>
    internal SqlExistsExpression(SqlQueryBodyExpression subquery, bool isNegated, Location? location)
        : base(location)
    {
        Subquery = subquery;
        IsNegated = isNegated;
        Depth = 1 + subquery.ExpressionDepth;
    }

    /// <summary>
    /// Gets the subquery being tested for existence.
    /// </summary>
    public SqlQueryBodyExpression Subquery { get; }

    /// <summary>
    /// Gets whether the predicate is negated (<c>NOT EXISTS</c>).
    /// </summary>
    public bool IsNegated { get; }

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Sql.Language.SqlExistsExpression _target;

        public DebugView(global::Assimalign.Cohesion.Database.Sql.Language.SqlExistsExpression value)
        {
            _target = value;
        }

        public global::Assimalign.Cohesion.Database.Sql.Language.SqlQueryBodyExpression Subquery => _target.Subquery;
        public bool IsNegated => _target.IsNegated;
    }
}
