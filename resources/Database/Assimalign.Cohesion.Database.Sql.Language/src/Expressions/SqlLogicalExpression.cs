using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Sql.Language;

using Assimalign.Cohesion.Database.Language;

/// <summary>
/// Represents a chain of operands joined by one logical operator, such as
/// <c>a = 1 AND b = 2 AND c = 3</c> or <c>x = 1 OR x = 2 OR x = 3</c>: one node over all of its
/// terms, however many there are (#1151).
/// </summary>
/// <remarks>
/// <para>
/// The parser builds one node for each run of <c>AND</c> (or of <c>OR</c>) terms at one level
/// of the precedence ladder, as PostgreSQL does, so a chain of any length is one level of the
/// expression tree and costs one level of the nesting limit. A parenthesized chain of the same
/// operator that opens a chain merges into it, the way left associativity read it before
/// chains were n-ary: <c>(a AND b) AND c</c> is the same node as <c>a AND b AND c</c>. A
/// parenthesized chain in any later position stays a nested node: <c>a AND (b AND c)</c> has
/// two. <c>NOT</c> is a <see cref="SqlUnaryExpression"/>, and the parser no longer builds a
/// <see cref="SqlBinaryExpression"/> with <see cref="SqlBinaryOperator.And"/> or
/// <see cref="SqlBinaryOperator.Or"/>.
/// </para>
/// <para>
/// The engine evaluates the operands from first to last and stops at the first that decides
/// the result (a <see langword="false"/> operand of <c>AND</c>, a <see langword="true"/> operand
/// of <c>OR</c>), under three-valued logic: the same order, the same result and the same faults
/// as the nested binary operators it replaces.
/// </para>
/// </remarks>
public sealed class SqlLogicalExpression : SqlExpression
{
    /// <summary>
    /// Initializes a new <see cref="SqlLogicalExpression"/>.
    /// </summary>
    /// <param name="op">The operator joining the operands.</param>
    /// <param name="operands">The operands in source order; at least two.</param>
    /// <param name="location">The source location: the first operator of the chain.</param>
    internal SqlLogicalExpression(SqlLogicalOperator op, IReadOnlyList<SqlExpression> operands, Location? location)
        : base(location)
    {
        Operator = op;
        Operands = operands;
        Depth = 1 + DepthOf(operands);
    }

    /// <summary>
    /// Gets the operator that joins the operands.
    /// </summary>
    public SqlLogicalOperator Operator { get; }

    /// <summary>
    /// Gets the operands in source order. A parsed chain has at least two.
    /// </summary>
    public IReadOnlyList<SqlExpression> Operands { get; }
}
