namespace Assimalign.Cohesion.Database.Documents.Language;

/// <summary>The optional input quantifier on an aggregate expression.</summary>
public enum OqlAggregateQuantifier
{
    /// <summary>No explicit quantifier; includes every admitted input.</summary>
    None,
    /// <summary>Explicit ALL; includes every admitted input.</summary>
    All,
    /// <summary>Includes each admitted value once, using grouping equality.</summary>
    Distinct,
}
