namespace Assimalign.Cohesion.Database.Sql.Language;

/// <summary>The set quantifier written inside a function call.</summary>
public enum SqlSetQuantifier
{
    /// <summary>Uses every input row.</summary>
    Default,
    /// <summary>Explicitly uses every input row.</summary>
    All,
    /// <summary>Uses each distinct argument tuple once.</summary>
    Distinct,
}
