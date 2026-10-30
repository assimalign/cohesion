using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// <c>[NOT] LIKE</c> or <c>[NOT] ILIKE</c> over a bound value and pattern, with their collation resolved when the
/// expression was bound.
/// </summary>
[global::System.Diagnostics.DebuggerDisplay("SqlBoundLike")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Internal.SqlBoundLike.DebugView))]
internal sealed class SqlBoundLike : SqlBoundExpression
{
    /// <summary>Initializes a bound pattern match.</summary>
    /// <param name="operand">The bound matched value.</param>
    /// <param name="pattern">The bound pattern.</param>
    /// <param name="escape">The bound escape expression.</param>
    /// <param name="compiled">The cached constant pattern, if any.</param>
    /// <param name="isNegated"><see langword="true"/> for <c>NOT LIKE</c>.</param>
    /// <param name="location">The source expression span.</param>
    /// <param name="collation">The collation of the value and the pattern.</param>
    /// <param name="isCaseInsensitive">Whether the predicate uses ILIKE folding.</param>
    internal SqlBoundLike(SqlBoundExpression operand, SqlBoundExpression pattern, SqlBoundExpression escape, bool isNegated, SqlBoundCollation collation, SqlLikePattern? compiled, Location? location = null, bool isCaseInsensitive = false)
        : base(SqlBoundExpressionKind.Like)
    {
        Location = location;
        Operand = operand;
        Pattern = pattern;
        Escape = escape;
        Compiled = compiled;
        IsNegated = isNegated;
        IsCaseInsensitive = isCaseInsensitive;
        Collation = collation;
    }

    /// <summary>Gets the bound matched value.</summary>
    internal SqlBoundExpression Operand { get; }

    /// <summary>Gets the bound pattern.</summary>
    internal SqlBoundExpression Pattern { get; }

    internal SqlBoundExpression Escape { get; }

    internal SqlLikePattern? Compiled { get; }

    /// <summary>Gets whether the match is <c>NOT LIKE</c>.</summary>
    internal bool IsNegated { get; }

    internal bool IsCaseInsensitive { get; }

    /// <summary>Gets the collation of the value and the pattern.</summary>
    internal SqlBoundCollation Collation { get; }

    internal Location? Location { get; }

    private sealed class DebugView
    {
        public DebugView(global::Assimalign.Cohesion.Database.Sql.Internal.SqlBoundLike value)
        {
            _ = value;
        }
    }
}
