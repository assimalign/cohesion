namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>The supported string predicate operators.</summary>
public enum GqlStringPredicateOperator
{
    /// <summary>Tests an ordinal prefix.</summary>
    StartsWith,
    /// <summary>Tests an ordinal suffix.</summary>
    EndsWith,
    /// <summary>Tests an ordinal substring.</summary>
    Contains,
}
