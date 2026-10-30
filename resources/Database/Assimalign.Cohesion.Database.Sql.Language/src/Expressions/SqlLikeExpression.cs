namespace Assimalign.Cohesion.Database.Sql.Language;

using Assimalign.Cohesion.Database.Language;

/// <summary>
/// Represents a <c>LIKE</c> or <c>ILIKE</c> predicate such as <c>name LIKE '%test%'</c>.
/// </summary>
[global::System.Diagnostics.DebuggerDisplay("SqlLikeExpression")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Language.SqlLikeExpression.DebugView))]
public sealed class SqlLikeExpression : SqlExpression
{
    /// <summary>
    /// Initializes a new <see cref="SqlLikeExpression"/>.
    /// </summary>
    /// <param name="operand">The expression being tested.</param>
    /// <param name="pattern">The pattern expression.</param>
    /// <param name="isNegated">Whether the predicate is negated (<c>NOT LIKE</c> or <c>NOT ILIKE</c>).</param>
    /// <param name="location">The source location.</param>
    /// <param name="escape">The explicit escape expression, or null for the default backslash.</param>
    /// <param name="isCaseInsensitive">Whether the predicate uses ILIKE case folding.</param>
    internal SqlLikeExpression(SqlExpression operand, SqlExpression pattern, bool isNegated, Location? location, SqlExpression? escape = null, bool isCaseInsensitive = false)
        : base(location)
    {
        Operand = operand;
        Pattern = pattern;
        Escape = escape;
        IsNegated = isNegated;
        IsCaseInsensitive = isCaseInsensitive;
        Depth = 1 + System.Math.Max(System.Math.Max(operand.Depth, pattern.Depth), escape?.Depth ?? 0);
    }

    /// <summary>
    /// Gets the expression being tested.
    /// </summary>
    public SqlExpression Operand { get; }

    /// <summary>
    /// Gets the pattern expression.
    /// </summary>
    public SqlExpression Pattern { get; }

    /// <summary>Gets the explicit ESCAPE expression, or null for the default backslash.</summary>
    public SqlExpression? Escape { get; }

    /// <summary>
    /// Gets whether the predicate is negated (<c>NOT LIKE</c> or <c>NOT ILIKE</c>).
    /// </summary>
    public bool IsNegated { get; }

    /// <summary>Gets whether this predicate uses collation-aware ILIKE case folding.</summary>
    public bool IsCaseInsensitive { get; }

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Sql.Language.SqlLikeExpression _target;

        public DebugView(global::Assimalign.Cohesion.Database.Sql.Language.SqlLikeExpression value)
        {
            _target = value;
        }

        public global::Assimalign.Cohesion.Database.Sql.Language.SqlExpression Operand => _target.Operand;
        public global::Assimalign.Cohesion.Database.Sql.Language.SqlExpression Pattern => _target.Pattern;
        public global::Assimalign.Cohesion.Database.Sql.Language.SqlExpression? Escape => _target.Escape;
        public bool IsNegated => _target.IsNegated;
        public bool IsCaseInsensitive => _target.IsCaseInsensitive;
    }
}
