using System.Collections.Generic;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Documents.Language;

/// <summary>Accesses metadata attached to a stored document, independently of its content.</summary>
[global::System.Diagnostics.DebuggerDisplay("OqlMetaExpression")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Documents.Language.OqlMetaExpression.DebugView))]
public sealed class OqlMetaExpression : OqlExpression
{
    /// <summary>Initializes a META expression.</summary>
    /// <param name="arguments">The optional source alias expression.</param>
    /// <param name="member">The metadata member, or null for a bare call.</param>
    /// <param name="location">The complete expression span.</param>
    public OqlMetaExpression(IReadOnlyList<OqlExpression> arguments, string? member, Location? location = null) : base(location)
    {
        Arguments = arguments;
        Member = member;
    }

    /// <summary>Gets the source arguments, validated by the planner.</summary>
    public IReadOnlyList<OqlExpression> Arguments { get; }
    /// <summary>Gets the requested metadata member.</summary>
    public string? Member { get; }

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Documents.Language.OqlMetaExpression _target;

        public DebugView(global::Assimalign.Cohesion.Database.Documents.Language.OqlMetaExpression value)
        {
            _target = value;
        }

        public global::System.Collections.Generic.IReadOnlyList<global::Assimalign.Cohesion.Database.Documents.Language.OqlExpression> Arguments => _target.Arguments;
        public string? Member => _target.Member;
        public string? Text => _target.Text;
        public global::Assimalign.Cohesion.Database.Language.Location? Location => _target.Location;
    }
}
