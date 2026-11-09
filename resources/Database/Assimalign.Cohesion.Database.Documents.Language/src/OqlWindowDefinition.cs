using System;

using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Documents.Language;

/// <summary>A query-local WINDOW declaration.</summary>
[global::System.Diagnostics.DebuggerDisplay("OqlWindowDefinition")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(OqlWindowDefinition.DebugView))]
public sealed class OqlWindowDefinition : OqlExpression
{
    /// <summary>Initializes a named window.</summary>
    /// <param name="name">The ordinal, case-sensitive name.</param>
    /// <param name="specification">The window specification.</param>
    /// <param name="location">The source span.</param>
    /// <exception cref="ArgumentNullException">A required member is null.</exception>
    public OqlWindowDefinition(string name, OqlWindowSpecification specification, Location? location = null) : base(location)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(specification);
        Name = name;
        Specification = specification;
    }
    /// <summary>Gets the declared name.</summary>
    public string Name { get; }
    /// <summary>Gets the declared specification.</summary>
    public OqlWindowSpecification Specification { get; }
    private sealed class DebugView
    {
        private readonly OqlWindowDefinition _target;
        public DebugView(OqlWindowDefinition target) { _target = target; }
        public string Name => _target.Name;
        public OqlWindowSpecification Specification => _target.Specification;
        public string? Text => _target.Text;
        public Location? Location => _target.Location;
    }
}
