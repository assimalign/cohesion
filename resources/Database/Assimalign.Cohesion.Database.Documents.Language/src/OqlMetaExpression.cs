using System.Collections.Generic;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Documents.Language;

/// <summary>Accesses metadata attached to a stored document, independently of its content.</summary>
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
}
