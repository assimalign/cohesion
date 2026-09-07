namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Selects the operation performed when a gateway application runs.
/// </summary>
public enum GatewayRunMode
{
    /// <summary>
    /// Realizes the application, supervises it until cancellation, and then stops it.
    /// </summary>
    Run,

    /// <summary>
    /// Reconciles the application to its desired state and exits.
    /// </summary>
    Apply,

    /// <summary>
    /// Removes the application from the selected target and exits.
    /// </summary>
    Teardown,

    /// <summary>
    /// Emits or applies the selected gateway's bootstrap resources and exits.
    /// </summary>
    Bootstrap,

    /// <summary>
    /// Writes the platform-neutral application model document and exits.
    /// </summary>
    Describe,

    /// <summary>
    /// Writes the selected gateway's compiled platform objects and exits.
    /// </summary>
    Render,
}
