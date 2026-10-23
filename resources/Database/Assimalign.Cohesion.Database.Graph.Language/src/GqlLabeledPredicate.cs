using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>
/// An ISO/IEC 39075 labeled predicate in <c>WHERE</c>: <c>n IS [NOT] LABELED A|B</c>, or its
/// colon form <c>n:A|B</c>. It is a Boolean primary over a bound node or relationship variable,
/// and a non-negated pure conjunction under the clause's top-level AND chain supplies required
/// labels for node anchors.
/// </summary>
[global::System.Diagnostics.DebuggerDisplay("GqlLabeledPredicate")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Graph.Language.GqlLabeledPredicate.DebugView))]
public sealed class GqlLabeledPredicate : GqlExpression
{
    /// <summary>Initializes a new instance of the <see cref="GqlLabeledPredicate"/> class.</summary>
    /// <param name="variable">The bound node or relationship variable the predicate tests.</param>
    /// <param name="labelExpression">The label expression the element must satisfy.</param>
    /// <param name="isNegated">Whether the predicate is <c>IS NOT LABELED</c>.</param>
    /// <param name="location">The source span.</param>
    public GqlLabeledPredicate(string variable, GqlLabelExpression labelExpression, bool isNegated = false,
        Location? location = null) : base(location)
    {
        Variable = variable;
        LabelExpression = labelExpression;
        IsNegated = isNegated;
    }

    /// <summary>Gets the binding name.</summary>
    public string Variable { get; }
    /// <summary>Gets the label expression, evaluated against a node's labels or a relationship's type.</summary>
    public GqlLabelExpression LabelExpression { get; }
    /// <summary>Gets whether the predicate is <c>IS NOT LABELED</c>: true for an element the expression does not match.</summary>
    public bool IsNegated { get; }

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Graph.Language.GqlLabeledPredicate _target;

        public DebugView(global::Assimalign.Cohesion.Database.Graph.Language.GqlLabeledPredicate value)
        {
            _target = value;
        }

        public string Variable => _target.Variable;
        public global::Assimalign.Cohesion.Database.Graph.Language.GqlLabelExpression LabelExpression => _target.LabelExpression;
        public bool IsNegated => _target.IsNegated;
        public string? Text => _target.Text;
        public global::Assimalign.Cohesion.Database.Language.Location? Location => _target.Location;
    }
}
