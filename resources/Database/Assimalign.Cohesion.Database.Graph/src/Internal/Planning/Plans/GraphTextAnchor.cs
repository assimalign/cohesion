namespace Assimalign.Cohesion.Database.Graph.Internal;

/// <summary>A bound text predicate whose access path is decided from this execution's pattern.</summary>
internal sealed record GraphTextAnchor(string Pattern, string Operation, bool Parameter)
{
    public bool CanSeek => GraphTextTrigrams.CanSeek(Pattern);
}
