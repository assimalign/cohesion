namespace Assimalign.Cohesion.Hosting.Resources;

/// <summary>
/// One action as a resource instance's manifest describes it.
/// </summary>
/// <param name="Name">The action's name.</param>
/// <param name="Kind">The operations the action accepts.</param>
/// <param name="IsStateful">
/// Whether a successful write leaves state that the gateway restores on instances started before
/// the resource is next deployed.
/// </param>
public sealed record ResourceActionDescriptor(
    ResourceActionName Name,
    ResourceActionKind Kind,
    bool IsStateful);
