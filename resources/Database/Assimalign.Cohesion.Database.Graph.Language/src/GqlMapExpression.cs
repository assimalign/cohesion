using System.Collections.Generic;
using System.Linq;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>A map value constructor with ordinal string keys.</summary>
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
}
