using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Sql.Language;

/// <summary>The partition and ordering of a window, optionally copied from an earlier definition.</summary>
[System.Diagnostics.DebuggerDisplay("SqlWindowSpecification")]
[System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
public sealed class SqlWindowSpecification
{
    internal SqlWindowSpecification(string? existingWindow, IReadOnlyList<SqlExpression> partitionBy, IReadOnlyList<SqlOrderByColumn> orderBy)
    {
        ExistingWindow = existingWindow;
        PartitionBy = partitionBy;
        OrderBy = orderBy;
    }
    /// <summary>Gets the earlier window being copied.</summary>
    public string? ExistingWindow { get; }
    /// <summary>Gets the partition keys.</summary>
    public IReadOnlyList<SqlExpression> PartitionBy { get; }
    /// <summary>Gets the ordering keys.</summary>
    public IReadOnlyList<SqlOrderByColumn> OrderBy { get; }
    internal int ExpressionDepth
    {
        get
        {
            int depth = SqlExpression.DepthOf(PartitionBy);
            foreach (var key in OrderBy) { depth = Math.Max(depth, key.Expression.Depth); }
            return depth;
        }
    }
    private sealed class DebugView
    {
        private readonly SqlWindowSpecification _value;
        public DebugView(SqlWindowSpecification value) { _value = value; }
        public string? ExistingWindow => _value.ExistingWindow;
        public IReadOnlyList<SqlExpression> PartitionBy => _value.PartitionBy;
        public IReadOnlyList<SqlOrderByColumn> OrderBy => _value.OrderBy;
    }
}
