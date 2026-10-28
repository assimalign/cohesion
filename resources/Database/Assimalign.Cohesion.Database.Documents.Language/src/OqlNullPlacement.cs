namespace Assimalign.Cohesion.Database.Documents.Language;

/// <summary>The written placement of the MISSING/NULL block in an ordering term.</summary>
public enum OqlNullPlacement
{
    /// <summary>Ascending places the block first; descending places it last.</summary>
    Default,
    /// <summary>Place the block before valued expressions.</summary>
    First,
    /// <summary>Place the block after valued expressions.</summary>
    Last,
}
