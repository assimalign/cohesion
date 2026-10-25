using System;
using System.Collections.Generic;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>A function call whose identity and kind are resolved by the engine catalog.</summary>
[System.Diagnostics.DebuggerDisplay("GqlNamedFunctionCallExpression {Name}")]
[System.Diagnostics.DebuggerTypeProxy(typeof(GqlNamedFunctionCallExpression.DebugView))]
public sealed class GqlNamedFunctionCallExpression : GqlExpression
{
    /// <summary>Initializes an immutable named call.</summary>
    /// <param name="name">The function identifier.</param><param name="arguments">The input expressions.</param>
    /// <param name="distinct">Whether an aggregate omits duplicate inputs.</param><param name="location">The source span.</param>
    public GqlNamedFunctionCallExpression(GraphFunctionName name, IEnumerable<GqlExpression> arguments, bool distinct = false, Location? location = null) : base(location)
    {
        if (!GraphFunctionName.IsValid(name.ToString(), out string? error)) { throw new ArgumentException(error, nameof(name)); }
        ArgumentNullException.ThrowIfNull(arguments);
        Name = name; Arguments = new List<GqlExpression>(arguments).AsReadOnly(); Distinct = distinct;
    }
    /// <summary>Gets the identifier.</summary>
    public GraphFunctionName Name { get; }
    /// <summary>Gets the immutable arguments.</summary>
    public IReadOnlyList<GqlExpression> Arguments { get; }
    /// <summary>Gets whether duplicate aggregate inputs are omitted.</summary>
    public bool Distinct { get; }

    private sealed class DebugView
    {
        private readonly GqlNamedFunctionCallExpression _target;
        public DebugView(GqlNamedFunctionCallExpression value) { _target = value; }
        public GraphFunctionName Name => _target.Name;
        public IReadOnlyList<GqlExpression> Arguments => _target.Arguments;
        public bool Distinct => _target.Distinct;
    }
}
