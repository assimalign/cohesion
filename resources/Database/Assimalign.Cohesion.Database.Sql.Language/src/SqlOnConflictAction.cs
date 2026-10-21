namespace Assimalign.Cohesion.Database.Sql.Language;

/// <summary>The action taken for an arbiter conflict.</summary>
public enum SqlOnConflictAction
{
    /// <summary>Skip the proposed row.</summary>
    DoNothing,
    /// <summary>Update the conflicting row.</summary>
    DoUpdate,
}
