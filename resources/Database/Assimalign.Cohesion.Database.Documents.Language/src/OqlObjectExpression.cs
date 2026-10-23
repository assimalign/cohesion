using System.Collections.Generic;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Documents.Language;

/// <summary>Constructs a JSON object, omitting members whose expressions evaluate to MISSING.</summary>
[global::System.Diagnostics.DebuggerDisplay("OqlObjectExpression")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Documents.Language.OqlObjectExpression.DebugView))]
public sealed class OqlObjectExpression : OqlExpression
{
    /// <summary>Initializes an object constructor.</summary>
    /// <param name="members">The named expressions in projection order.</param>
    /// <param name="location">The source span.</param>
    public OqlObjectExpression(IReadOnlyList<KeyValuePair<string, OqlExpression>> members, Location? location = null) : base(location)
    {
        Members = members;
    }

    /// <summary>Gets the object members in source order.</summary>
    public IReadOnlyList<KeyValuePair<string, OqlExpression>> Members { get; }

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Documents.Language.OqlObjectExpression _target;

        public DebugView(global::Assimalign.Cohesion.Database.Documents.Language.OqlObjectExpression value)
        {
            _target = value;
        }

        public global::System.Collections.Generic.IReadOnlyList<global::System.Collections.Generic.KeyValuePair<string, global::Assimalign.Cohesion.Database.Documents.Language.OqlExpression>> Members => _target.Members;
        public string? Text => _target.Text;
        public global::Assimalign.Cohesion.Database.Language.Location? Location => _target.Location;
    }
}
