namespace Assimalign.Cohesion.Database.Sql.Language;

using Assimalign.Cohesion.Database.Language;

/// <summary>Represents an IS [NOT] DISTINCT FROM predicate.</summary>
public sealed class SqlDistinctExpression : SqlExpression
{
    /// <summary>Initializes an IS [NOT] DISTINCT FROM predicate.</summary>
    /// <param name="left">The left comparand.</param>
    /// <param name="right">The right comparand.</param>
    /// <param name="isNegated">Whether the predicate includes NOT.</param>
    /// <param name="location">The source location.</param>
    internal SqlDistinctExpression(SqlExpression left, SqlExpression right, bool isNegated, Location? location)
        : base(location)
    {
        Left = left;
        Right = right;
        IsNegated = isNegated;
        Depth = 1 + System.Math.Max(left.Depth, right.Depth);
    }

    /// <summary>Gets the left comparand.</summary>
    public SqlExpression Left { get; }

    /// <summary>Gets the right comparand.</summary>
    public SqlExpression Right { get; }

    /// <summary>Gets whether the predicate includes NOT.</summary>
    public bool IsNegated { get; }
}
