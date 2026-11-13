using System;
using System.Collections.Generic;
using System.Linq;

using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Documents.Language;

/// <summary>A partition and ordering specification, optionally copied from a named window.</summary>
[global::System.Diagnostics.DebuggerDisplay("OqlWindowSpecification")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(OqlWindowSpecification.DebugView))]
public sealed class OqlWindowSpecification : OqlExpression
{
    /// <summary>Initializes a window specification.</summary>
    /// <param name="partitionBy">The partition keys.</param>
    /// <param name="orderBy">The ordering keys.</param>
    /// <param name="reference">The earlier window to copy.</param>
    /// <param name="location">The source span.</param>
    /// <exception cref="ArgumentNullException">A list or list member is null.</exception>
    public OqlWindowSpecification(IReadOnlyList<OqlExpression> partitionBy, IReadOnlyList<OqlOrdering> orderBy, string? reference = null, Location? location = null) : base(location)
    {
        ArgumentNullException.ThrowIfNull(partitionBy);
        ArgumentNullException.ThrowIfNull(orderBy);
        foreach (var key in partitionBy) { ArgumentNullException.ThrowIfNull(key); }
        foreach (var order in orderBy) { ArgumentNullException.ThrowIfNull(order); ArgumentNullException.ThrowIfNull(order.Expression); }
        PartitionBy = Array.AsReadOnly(partitionBy.ToArray());
        OrderBy = Array.AsReadOnly(orderBy.ToArray());
        Reference = reference;
    }

    /// <summary>Gets the partition keys.</summary>
    public IReadOnlyList<OqlExpression> PartitionBy { get; }
    /// <summary>Gets the ordering keys.</summary>
    public IReadOnlyList<OqlOrdering> OrderBy { get; }
    /// <summary>Gets the earlier window name to copy.</summary>
    public string? Reference { get; }
    private sealed class DebugView
    {
        private readonly OqlWindowSpecification _target;
        public DebugView(OqlWindowSpecification target) { _target = target; }
        public IReadOnlyList<OqlExpression> PartitionBy => _target.PartitionBy;
        public IReadOnlyList<OqlOrdering> OrderBy => _target.OrderBy;
        public string? Reference => _target.Reference;
        public string? Text => _target.Text;
        public Location? Location => _target.Location;
    }
}
