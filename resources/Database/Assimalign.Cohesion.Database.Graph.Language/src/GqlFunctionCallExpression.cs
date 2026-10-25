using System.Collections.Generic;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>A resolved scalar function call, including niladic transaction datetime functions.</summary>
[global::System.Diagnostics.DebuggerDisplay("GqlFunctionCallExpression")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Graph.Language.GqlFunctionCallExpression.DebugView))]
public sealed class GqlFunctionCallExpression : GqlExpression
{
    /// <summary>Initializes a function call over a copy of its arguments.</summary>
    /// <param name="function">The resolved function identity.</param>
    /// <param name="arguments">The arguments in source order; empty for a niladic function.</param>
    /// <param name="location">The complete source span.</param>
    public GqlFunctionCallExpression(GqlScalarFunction function, IEnumerable<GqlExpression> arguments, Location? location = null) : base(location)
    {
        Function = function;
        Arguments = arguments is null ? null! : new List<GqlExpression>(arguments).AsReadOnly();
    }

    /// <summary>Gets the resolved function identity.</summary>
    public GqlScalarFunction Function { get; }
    /// <summary>Gets the arguments in source order.</summary>
    public IReadOnlyList<GqlExpression> Arguments { get; }
    /// <summary>Gets whether the function is written without parentheses.</summary>
    public bool IsNiladic => Function is GqlScalarFunction.CurrentDate or GqlScalarFunction.CurrentTimestamp;

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Graph.Language.GqlFunctionCallExpression _target;

        public DebugView(global::Assimalign.Cohesion.Database.Graph.Language.GqlFunctionCallExpression value)
        {
            _target = value;
        }

        public global::Assimalign.Cohesion.Database.Graph.Language.GqlScalarFunction Function => _target.Function;
        public global::System.Collections.Generic.IReadOnlyList<global::Assimalign.Cohesion.Database.Graph.Language.GqlExpression> Arguments => _target.Arguments;
        public bool IsNiladic => _target.IsNiladic;
        public string? Text => _target.Text;
        public global::Assimalign.Cohesion.Database.Language.Location? Location => _target.Location;
    }
}
