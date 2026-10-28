using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>An ordered fold with lexical accumulator and iterator bindings.</summary>
[global::System.Diagnostics.DebuggerDisplay("GqlReduceExpression")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(GqlReduceExpression.DebugView))]
public sealed class GqlReduceExpression : GqlExpression
{
    /// <summary>Initializes an ordered list reduction.</summary>
    /// <param name="accumulator">The accumulator name, visible only in the body.</param>
    /// <param name="initial">The seed, evaluated in the enclosing scope for a non-NULL source.</param>
    /// <param name="variable">The iterator name, visible only in the body.</param>
    /// <param name="list">The source list, evaluated in the enclosing scope.</param>
    /// <param name="body">The expression producing the next accumulator value.</param>
    /// <param name="location">The optional source span.</param>
    public GqlReduceExpression(string accumulator, GqlExpression initial, string variable, GqlExpression list,
        GqlExpression body, Location? location = null) : base(location)
    {
        Accumulator = accumulator;
        Initial = initial;
        Variable = variable;
        List = list;
        Body = body;
    }

    /// <summary>Gets the accumulator name.</summary>
    public string Accumulator { get; }
    /// <summary>Gets the seed expression.</summary>
    public GqlExpression Initial { get; }
    /// <summary>Gets the iterator name.</summary>
    public string Variable { get; }
    /// <summary>Gets the source list.</summary>
    public GqlExpression List { get; }
    /// <summary>Gets the next-value expression.</summary>
    public GqlExpression Body { get; }

    private sealed class DebugView
    {
        private readonly GqlReduceExpression _target;

        public DebugView(GqlReduceExpression value) { _target = value; }

        public string Accumulator => _target.Accumulator;
        public GqlExpression Initial => _target.Initial;
        public string Variable => _target.Variable;
        public GqlExpression List => _target.List;
        public GqlExpression Body => _target.Body;
        public string? Text => _target.Text;
        public Location? Location => _target.Location;
    }
}
