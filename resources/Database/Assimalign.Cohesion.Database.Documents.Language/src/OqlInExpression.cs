using System.Collections.Generic;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Documents.Language;

/// <summary>A structural membership predicate over an explicit list or an array value.</summary>
[global::System.Diagnostics.DebuggerDisplay("OqlInExpression")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Documents.Language.OqlInExpression.DebugView))]
public sealed class OqlInExpression : OqlExpression
{
    /// <summary>Initializes an IN predicate.</summary>
    /// <param name="operand">The value to find.</param>
    /// <param name="values">The list items; one array-valued item remains one item.</param>
    /// <param name="negated">Whether this is NOT IN.</param>
    /// <param name="location">The source span.</param>
    public OqlInExpression(OqlExpression operand, IReadOnlyList<OqlExpression> values, bool negated = false, Location? location = null) : base(location)
    {
        Operand = operand;
        Values = values;
        Negated = negated;
    }

    /// <summary>Initializes membership over an array value.</summary>
    /// <param name="operand">The value to find.</param>
    /// <param name="array">The array expression.</param>
    /// <param name="negated">Whether this is NOT IN.</param>
    /// <param name="location">The source span.</param>
    public OqlInExpression(OqlExpression operand, OqlExpression array, bool negated = false, Location? location = null) : base(location)
    {
        Operand = operand;
        Values = [];
        Array = array;
        Negated = negated;
    }

    /// <summary>Gets the value to find.</summary>
    public OqlExpression Operand { get; }
    /// <summary>Gets the explicit list items.</summary>
    public IReadOnlyList<OqlExpression> Values { get; }
    /// <summary>Gets the array operand, or null for a parenthesized list.</summary>
    public OqlExpression? Array { get; }
    /// <summary>Gets whether the result is negated.</summary>
    public bool Negated { get; }

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Documents.Language.OqlInExpression _target;

        public DebugView(global::Assimalign.Cohesion.Database.Documents.Language.OqlInExpression value)
        {
            _target = value;
        }

        public global::Assimalign.Cohesion.Database.Documents.Language.OqlExpression Operand => _target.Operand;
        public global::System.Collections.Generic.IReadOnlyList<global::Assimalign.Cohesion.Database.Documents.Language.OqlExpression> Values => _target.Values;
        public global::Assimalign.Cohesion.Database.Documents.Language.OqlExpression? Array => _target.Array;
        public bool Negated => _target.Negated;
        public string? Text => _target.Text;
        public global::Assimalign.Cohesion.Database.Language.Location? Location => _target.Location;
    }
}
