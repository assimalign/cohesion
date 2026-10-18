using Assimalign.Cohesion.Database.Sql.Language;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>The bound Distinct predicate.</summary>
internal sealed class SqlBoundDistinct : SqlBoundExpression
{
    internal SqlBoundDistinct(SqlBoundExpression left, SqlBoundExpression right, bool isNegated, SqlBoundCollation collation)
        : base(SqlBoundExpressionKind.Distinct)
    {
        Left = left;
        Right = right;
        IsNegated = isNegated;
        Collation = collation;
    }

    internal SqlBoundExpression Left { get; }

    internal SqlBoundExpression Right { get; }

    internal bool IsNegated { get; }

    internal SqlBoundCollation Collation { get; }
}
