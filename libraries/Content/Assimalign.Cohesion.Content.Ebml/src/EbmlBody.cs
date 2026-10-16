using System.Collections.Generic;

namespace Assimalign.Cohesion.Content.Ebml;

/// <summary>The ordered root-level body elements, including preserved unknown elements and Void.</summary>
public sealed class EbmlBody
{
    /// <summary>Gets the editable ordered body elements.</summary>
    public IList<EbmlElement> Elements { get; } = new List<EbmlElement>();
}
