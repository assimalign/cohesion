namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// The outcome of executing an action or restoring its state.
/// </summary>
public enum ResourceActionStatus
{
    /// <summary>The action ran, or the state was restored.</summary>
    Succeeded,

    /// <summary>The request was refused: its input or the instance's state did not allow it.</summary>
    Rejected,

    /// <summary>The resource has no action with the requested name.</summary>
    NotFound,

    /// <summary>The action does not accept the requested operation, or has no state to restore.</summary>
    NotSupported,
}
