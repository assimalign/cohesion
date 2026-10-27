using System.Collections.Generic;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Documents.Language;

/// <summary>Projects each document affected by a write statement.</summary>
[global::System.Diagnostics.DebuggerDisplay("OqlReturningExpression")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(OqlReturningExpression.DebugView))]
public sealed class OqlReturningExpression : OqlExpression
{
    /// <summary>Initializes a RETURNING projection.</summary>
    /// <param name="projections">The expressions and optional output names, in order.</param>
    /// <param name="isValue">Whether RAW or VALUE requests one unwrapped value.</param>
    /// <param name="location">The complete clause span.</param>
    public OqlReturningExpression(IReadOnlyList<OqlProjection> projections, bool isValue = false, Location? location = null)
        : base(location)
    {
        Projections = projections;
        IsValue = isValue;
    }

    /// <summary>Gets the projected expressions and output names.</summary>
    public IReadOnlyList<OqlProjection> Projections { get; }

    /// <summary>Gets whether the clause returns one unwrapped JSON value.</summary>
    public bool IsValue { get; }

    private sealed class DebugView
    {
        private readonly OqlReturningExpression _target;
        public DebugView(OqlReturningExpression value) { _target = value; }
        public IReadOnlyList<OqlProjection> Projections => _target.Projections;
        public bool IsValue => _target.IsValue;
        public string? Text => _target.Text;
        public Location? Location => _target.Location;
    }
}
