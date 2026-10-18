using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Sql.Language;

/// <summary>Represents a binary UNION, INTERSECT or EXCEPT query expression.</summary>
public sealed class SqlSetOperationExpression : SqlQueryBodyExpression
{
    internal SqlSetOperationExpression(SqlQueryBodyExpression left, SqlQueryBodyExpression right,
        SqlSetOperator operation, bool isAll, Location? location) : base(null, location)
    {
        Left = left;
        Right = right;
        Operator = operation;
        IsAll = isAll;
        ExpressionDepth = 1 + System.Math.Max(left.ExpressionDepth, right.ExpressionDepth);
    }

    /// <summary>Gets the left query operand, which supplies output names.</summary>
    public SqlQueryBodyExpression Left { get; }

    /// <summary>Gets the right query operand.</summary>
    public SqlQueryBodyExpression Right { get; }

    /// <summary>Gets the set operator.</summary>
    public SqlSetOperator Operator { get; }

    /// <summary>Gets whether ALL preserves the operator's bag multiplicities.</summary>
    public bool IsAll { get; }
}

/// <summary>The SQL binary set operators.</summary>
public enum SqlSetOperator
{
    /// <summary>Combines rows from both inputs.</summary>
    Union,
    /// <summary>Retains left rows absent from the right input.</summary>
    Except,
    /// <summary>Retains rows present in both inputs; binds tighter than UNION and EXCEPT.</summary>
    Intersect,
}
