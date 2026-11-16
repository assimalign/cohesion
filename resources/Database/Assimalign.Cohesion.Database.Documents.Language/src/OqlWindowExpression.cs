using System;

using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Documents.Language;

/// <summary>A function evaluated over a complete window partition.</summary>
[global::System.Diagnostics.DebuggerDisplay("OqlWindowExpression")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(OqlWindowExpression.DebugView))]
public sealed class OqlWindowExpression : OqlExpression
{
    /// <summary>Initializes an OVER expression.</summary>
    /// <param name="call">The window function call.</param>
    /// <param name="specification">The inline or copied specification; null for a direct named reference.</param>
    /// <param name="name">The direct window name; null for a parenthesized specification.</param>
    /// <param name="location">The source span.</param>
    /// <exception cref="ArgumentNullException">The call is null.</exception>
    public OqlWindowExpression(OqlCallExpression call, OqlWindowSpecification? specification = null, string? name = null, Location? location = null) : base(location)
    {
        ArgumentNullException.ThrowIfNull(call);
        Call = call;
        Specification = specification;
        Name = name;
    }

    /// <summary>Gets the function call.</summary>
    public OqlCallExpression Call { get; }
    /// <summary>Gets the parenthesized specification, or null for OVER name.</summary>
    public OqlWindowSpecification? Specification { get; }
    /// <summary>Gets the direct reference name.</summary>
    public string? Name { get; }
    private sealed class DebugView
    {
        private readonly OqlWindowExpression _target;
        public DebugView(OqlWindowExpression target) { _target = target; }
        public OqlCallExpression Call => _target.Call;
        public OqlWindowSpecification? Specification => _target.Specification;
        public string? Name => _target.Name;
        public string? Text => _target.Text;
        public Location? Location => _target.Location;
    }
}
