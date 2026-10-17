using System.Collections.Generic;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Documents.Language;

/// <summary>Constructs a JSON object, omitting members whose expressions evaluate to MISSING.</summary>
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
}
