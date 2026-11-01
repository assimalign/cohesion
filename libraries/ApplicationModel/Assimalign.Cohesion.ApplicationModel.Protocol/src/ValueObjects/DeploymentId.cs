namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Identifies one deployment of a resource: the inputs a gateway deployed it with. Every instance the
/// deployment starts receives the id with its environment and repeats it in the manifest it publishes.
/// </summary>
/// <remarks>
/// The gateway that deploys a resource derives the id from what it deploys, so deploying the same
/// inputs again yields the same id. It counts the deployment ready only once an instance that repeats
/// the id publishes, so an instance an earlier deployment left running, such as a pod of the previous
/// rollout that restarts, cannot stand in for this one.
/// </remarks>
public readonly partial struct DeploymentId
{
}
