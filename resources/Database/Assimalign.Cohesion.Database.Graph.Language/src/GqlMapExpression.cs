using System.Collections.Generic;
using System.Linq;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>A map value constructor with ordinal string keys.</summary>
[global::System.Diagnostics.DebuggerDisplay("GqlMapExpression")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Graph.Language.GqlMapExpression.DebugView))]
public sealed class GqlMapExpression : GqlExpression
{
    /// <summary>Initializes the expression.</summary>
    /// <param name="entries">The named value expressions.</param>
    /// <param name="location">The optional source span.</param>
    public GqlMapExpression(IEnumerable<KeyValuePair<string, GqlExpression>> entries, Location? location = null) : base(location)
    {
        Entries = entries is null ? null! : System.Array.AsReadOnly(entries.ToArray());
    }

    /// <summary>Gets the entries in source order.</summary>
    public IReadOnlyList<KeyValuePair<string, GqlExpression>> Entries { get; }

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Graph.Language.GqlMapExpression _target;

        public DebugView(global::Assimalign.Cohesion.Database.Graph.Language.GqlMapExpression value)
        {
            _target = value;
        }

        public global::System.Collections.Generic.IReadOnlyList<global::System.Collections.Generic.KeyValuePair<string, global::Assimalign.Cohesion.Database.Graph.Language.GqlExpression>> Entries => _target.Entries;
        public string? Text => _target.Text;
        public global::Assimalign.Cohesion.Database.Language.Location? Location => _target.Location;
    }
}
