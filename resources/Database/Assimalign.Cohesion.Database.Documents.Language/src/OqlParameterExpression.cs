using System.Collections.Generic;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Documents.Language;

/// <summary>A named or positional query parameter.</summary>
[global::System.Diagnostics.DebuggerDisplay("OqlParameterExpression {Name,nq}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Documents.Language.OqlParameterExpression.DebugView))]
public sealed class OqlParameterExpression : OqlExpression
{
    /// <summary>Initializes a new instance of the <see cref="OqlParameterExpression"/> class.</summary>
    /// <param name="name">The parameter name without its dollar or at-sign prefix.</param>
    /// <param name="location">The source span.</param>
    public OqlParameterExpression(string name, Location? location = null) : base(location)
    {
        Name = name;
    }

    /// <summary>Gets the parameter name without its prefix.</summary>
    public string Name { get; }

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Documents.Language.OqlParameterExpression _target;

        public DebugView(global::Assimalign.Cohesion.Database.Documents.Language.OqlParameterExpression value)
        {
            _target = value;
        }

        public string Name => _target.Name;
        public string? Text => _target.Text;
        public global::Assimalign.Cohesion.Database.Language.Location? Location => _target.Location;
    }
}
