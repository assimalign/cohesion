using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>A list slice with inclusive start and exclusive end.</summary>
/// <remarks>Either bound may be omitted; planning requires at least one bound.</remarks>
[global::System.Diagnostics.DebuggerDisplay("GqlListSliceExpression")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(GqlListSliceExpression.DebugView))]
public sealed class GqlListSliceExpression : GqlExpression
{
    /// <summary>Initializes a list slice.</summary>
    /// <param name="operand">The source list.</param>
    /// <param name="from">The optional inclusive start; negative values count from the end.</param>
    /// <param name="to">The optional exclusive end; negative values count from the end.</param>
    /// <param name="location">The optional source span.</param>
    public GqlListSliceExpression(GqlExpression operand, GqlExpression? from = null, GqlExpression? to = null,
        Location? location = null) : base(location)
    {
        Operand = operand;
        From = from;
        To = to;
    }

    /// <summary>Gets the source list.</summary>
    public GqlExpression Operand { get; }
    /// <summary>Gets the optional inclusive start.</summary>
    public GqlExpression? From { get; }
    /// <summary>Gets the optional exclusive end.</summary>
    public GqlExpression? To { get; }

    private sealed class DebugView
    {
        private readonly GqlListSliceExpression _target;

        public DebugView(GqlListSliceExpression value) { _target = value; }

        public GqlExpression Operand => _target.Operand;
        public GqlExpression? From => _target.From;
        public GqlExpression? To => _target.To;
        public string? Text => _target.Text;
        public Location? Location => _target.Location;
    }
}
