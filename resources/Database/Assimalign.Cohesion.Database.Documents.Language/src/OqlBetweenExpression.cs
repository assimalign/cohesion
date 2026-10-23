using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Documents.Language;

/// <summary>An inclusive range predicate.</summary>
[global::System.Diagnostics.DebuggerDisplay("OqlBetweenExpression")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Documents.Language.OqlBetweenExpression.DebugView))]
public sealed class OqlBetweenExpression : OqlExpression
{
    /// <summary>Initializes a BETWEEN predicate.</summary>
    /// <param name="operand">The value to compare.</param>
    /// <param name="lower">The inclusive lower bound.</param>
    /// <param name="upper">The inclusive upper bound.</param>
    /// <param name="negated">Whether this is NOT BETWEEN.</param>
    /// <param name="location">The source span.</param>
    public OqlBetweenExpression(OqlExpression operand, OqlExpression lower, OqlExpression upper, bool negated = false, Location? location = null) : base(location)
    {
        Operand = operand;
        Lower = lower;
        Upper = upper;
        Negated = negated;
    }

    /// <summary>Gets the value to compare.</summary>
    public OqlExpression Operand { get; }
    /// <summary>Gets the inclusive lower bound.</summary>
    public OqlExpression Lower { get; }
    /// <summary>Gets the inclusive upper bound.</summary>
    public OqlExpression Upper { get; }
    /// <summary>Gets whether the result is negated.</summary>
    public bool Negated { get; }

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Documents.Language.OqlBetweenExpression _target;

        public DebugView(global::Assimalign.Cohesion.Database.Documents.Language.OqlBetweenExpression value)
        {
            _target = value;
        }

        public global::Assimalign.Cohesion.Database.Documents.Language.OqlExpression Operand => _target.Operand;
        public global::Assimalign.Cohesion.Database.Documents.Language.OqlExpression Lower => _target.Lower;
        public global::Assimalign.Cohesion.Database.Documents.Language.OqlExpression Upper => _target.Upper;
        public bool Negated => _target.Negated;
        public string? Text => _target.Text;
        public global::Assimalign.Cohesion.Database.Language.Location? Location => _target.Location;
    }
}
