using System;

using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Documents.Language;

/// <summary>An ARRAY or FIRST mapping over one lexically scoped array binding.</summary>
[global::System.Diagnostics.DebuggerDisplay("OqlComprehensionExpression")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(OqlComprehensionExpression.DebugView))]
public sealed class OqlComprehensionExpression : OqlExpression
{
    /// <summary>Initializes an array comprehension.</summary>
    /// <param name="variable">The variable visible in the mapping and WHEN predicate.</param>
    /// <param name="collection">The array evaluated in the enclosing scope.</param>
    /// <param name="projection">The expression mapped for each qualifying element.</param>
    /// <param name="predicate">The optional WHEN predicate.</param>
    /// <param name="isFirst">Whether to return only the first qualifying mapping.</param>
    /// <param name="location">The source span.</param>
    /// <exception cref="ArgumentNullException">A required expression or variable is null.</exception>
    public OqlComprehensionExpression(string variable, OqlExpression collection, OqlExpression projection,
        OqlExpression? predicate = null, bool isFirst = false, Location? location = null) : base(location)
    {
        ArgumentNullException.ThrowIfNull(variable);
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentNullException.ThrowIfNull(projection);
        Variable = variable;
        Collection = collection;
        Projection = projection;
        Predicate = predicate;
        IsFirst = isFirst;
    }

    /// <summary>Gets the lexically scoped variable name.</summary>
    public string Variable { get; }
    /// <summary>Gets the input evaluated before introducing the variable.</summary>
    public OqlExpression Collection { get; }
    /// <summary>Gets the mapped expression evaluated in the inner scope.</summary>
    public OqlExpression Projection { get; }
    /// <summary>Gets the optional WHEN predicate evaluated in the inner scope.</summary>
    public OqlExpression? Predicate { get; }
    /// <summary>Gets whether this is FIRST rather than ARRAY.</summary>
    public bool IsFirst { get; }

    private sealed class DebugView
    {
        private readonly OqlComprehensionExpression _target;

        public DebugView(OqlComprehensionExpression value) => _target = value;

        public string Variable => _target.Variable;
        public OqlExpression Collection => _target.Collection;
        public OqlExpression Projection => _target.Projection;
        public OqlExpression? Predicate => _target.Predicate;
        public bool IsFirst => _target.IsFirst;
        public string? Text => _target.Text;
        public Location? Location => _target.Location;
    }
}
