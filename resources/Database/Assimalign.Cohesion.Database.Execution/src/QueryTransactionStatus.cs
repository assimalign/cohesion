namespace Assimalign.Cohesion.Database.Execution;

/// <summary>
/// The observable status of a <see cref="IQueryTransactionScope"/>. Transitions are
/// explicit: Active → Committed | RolledBack | Faulted, and completed scopes never
/// transition again.
/// </summary>
public enum QueryTransactionStatus : byte
{
    /// <summary>The scope is active and accepting operations.</summary>
    Active = 0,

    /// <summary>The scope committed durably.</summary>
    Committed,

    /// <summary>The scope was rolled back.</summary>
    RolledBack,

    /// <summary>The scope failed while committing or rolling back.</summary>
    Faulted,
}
