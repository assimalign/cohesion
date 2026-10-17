namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>Controls duplicate rows in a RETURN result.</summary>
public enum GqlSetQuantifier
{
    /// <summary>Retains every projected row; the default when no quantifier is written.</summary>
    All,
    /// <summary>Retains one row for each distinct tuple of projected values.</summary>
    Distinct,
}
