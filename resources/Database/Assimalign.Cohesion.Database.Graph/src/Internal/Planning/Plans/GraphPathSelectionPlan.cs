using System.Collections.Generic;

using Assimalign.Cohesion.Database.Graph.Language;

namespace Assimalign.Cohesion.Database.Graph.Internal;

// Partition by the actual source-ordered endpoint identities, even for anonymous endpoints,
// independently for each incoming binding. An interior scan anchor never changes that pair.
internal sealed record GraphPathSelectionPlan(GqlPathSelector Selector, GraphPathShape Search,
    GqlPathMode Mode, IReadOnlyList<string> StartVariables, IReadOnlyList<string> EndVariables,
    IReadOnlyList<string> BoundEndpoints)
{
    public bool OrderByLength => Selector.Kind is not (GqlPathSelectorKind.Any or GqlPathSelectorKind.All);
    public bool CountLengthGroups => Selector.Kind is GqlPathSelectorKind.AllShortest or GqlPathSelectorKind.ShortestGroups;
    public long? Count => Selector.Kind == GqlPathSelectorKind.All ? null : Selector.Count;
}
