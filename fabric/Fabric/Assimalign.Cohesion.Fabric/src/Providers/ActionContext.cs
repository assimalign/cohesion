using System.Text.Json;

namespace Assimalign.Cohesion.Fabric;

/// <summary>
/// One invocation of a resource action, such as <c>rotate-signing-key</c>.
/// </summary>
/// <param name="Resource">The resource.</param>
/// <param name="Environment">The environment it runs in.</param>
/// <param name="Action">The action name, as declared by <see cref="ResourceActionAttribute"/>.</param>
/// <param name="Arguments">The action's arguments, if it takes any.</param>
/// <param name="Operation">The operation the action reports under.</param>
public sealed record ActionContext(
    ResourceView Resource,
    FabricEnvironment Environment,
    string Action,
    JsonElement? Arguments,
    FabricOperation Operation);
