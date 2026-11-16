using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Documents.Language;

/// <summary>Combines two query values with a SQL++ set operation.</summary>
[System.Diagnostics.DebuggerDisplay("OqlSetOperationExpression {Operation}")]
[System.Diagnostics.DebuggerTypeProxy(typeof(OqlSetOperationExpression.DebugView))]
public sealed class OqlSetOperationExpression : OqlQueryExpression
{
    /// <summary>Initializes a compound query. All set operators have equal, left-associative precedence.</summary>
    /// <param name="left">The left operand, including any parenthesized local paging.</param>
    /// <param name="right">The right operand, including any parenthesized local paging.</param>
    /// <param name="operation">The set operation.</param>
    /// <param name="isAll">Whether duplicate occurrences are preserved.</param>
    /// <param name="orderBy">The compound ordering expressions, or null for none.</param>
    /// <param name="limit">The compound maximum row count.</param>
    /// <param name="offset">The compound row offset.</param>
    /// <param name="location">The source span.</param>
    /// <exception cref="ArgumentNullException">An operand is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The operation is not defined.</exception>
    public OqlSetOperationExpression(OqlQueryExpression left, OqlQueryExpression right,
        OqlSetOperation operation, bool isAll = false, IReadOnlyList<OqlOrdering>? orderBy = null,
        OqlExpression? limit = null, OqlExpression? offset = null, Location? location = null)
        : base(orderBy ?? [], left?.IsValue == true && right?.IsValue == true, limit, offset, location)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        if (operation is not (OqlSetOperation.Union or OqlSetOperation.Intersect or OqlSetOperation.Except))
        { throw new ArgumentOutOfRangeException(nameof(operation)); }
        Left = left;
        Right = right;
        Operation = operation;
        IsAll = isAll;
    }

    /// <summary>Gets the left query operand.</summary>
    public OqlQueryExpression Left { get; }
    /// <summary>Gets the right query operand.</summary>
    public OqlQueryExpression Right { get; }
    /// <summary>Gets the set operation. INTERSECT and EXCEPT are currently recognized but refused.</summary>
    public OqlSetOperation Operation { get; }
    /// <summary>Gets whether duplicate occurrences are preserved.</summary>
    public bool IsAll { get; }

    private sealed class DebugView
    {
        private readonly OqlSetOperationExpression _target;
        public DebugView(OqlSetOperationExpression value) { _target = value; }
        public OqlQueryExpression Left => _target.Left;
        public OqlQueryExpression Right => _target.Right;
        public OqlSetOperation Operation => _target.Operation;
        public bool IsAll => _target.IsAll;
        public bool IsValue => _target.IsValue;
        public IReadOnlyList<OqlOrdering> OrderBy => _target.OrderBy;
        public OqlExpression? Limit => _target.Limit;
        public OqlExpression? Offset => _target.Offset;
        public string? Text => _target.Text;
        public Location? Location => _target.Location;
    }
}

/// <summary>The SQL++ set operations, with equal precedence.</summary>
public enum OqlSetOperation
{
    /// <summary>Concatenates operands, eliminating duplicates unless ALL is specified.</summary>
    Union,
    /// <summary>Keeps values present in both operands; execution is deferred.</summary>
    Intersect,
    /// <summary>Keeps values from the left operand absent from the right; execution is deferred.</summary>
    Except,
}
