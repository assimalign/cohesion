namespace Assimalign.Cohesion.Database.Sql.Language;

/// <summary>NULL treatment for navigation and frame value functions.</summary>
public enum SqlWindowNullTreatment
{
    /// <summary>Use the function default, RESPECT NULLS.</summary>
    Default,
    /// <summary>Skip NULL argument values when selecting a member.</summary>
    Ignore,
    /// <summary>Count NULL argument values as members.</summary>
    Respect,
}
