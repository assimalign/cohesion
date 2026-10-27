using System.Globalization;

using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>A MATCH path selection, applied independently to each ordered endpoint pair.</summary>
/// <param name="Kind">The selection rule.</param>
/// <param name="Count">The positive path or distinct-length-group count; one for uncounted forms.</param>
/// <param name="Location">The selector prefix's source span, including its optional mode and path tokens.</param>
public sealed record GqlPathSelector(GqlPathSelectorKind Kind, long Count = 1, Location? Location = null)
{
    /// <summary>Returns the canonical selector spelling, without a mode or optional PATHS token.</summary>
    /// <returns>The GQL selector text.</returns>
    public override string ToString() => Kind switch
    {
        GqlPathSelectorKind.Any => "ANY",
        GqlPathSelectorKind.All => "ALL",
        GqlPathSelectorKind.AnyShortest => "ANY SHORTEST",
        GqlPathSelectorKind.AllShortest => "ALL SHORTEST",
        GqlPathSelectorKind.Shortest => "SHORTEST " + Count.ToString(CultureInfo.InvariantCulture),
        GqlPathSelectorKind.ShortestGroups => "SHORTEST " + Count.ToString(CultureInfo.InvariantCulture) + " GROUPS",
        _ => Kind.ToString(),
    };
}
