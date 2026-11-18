using System.Collections.Generic;

namespace Assimalign.Cohesion.Fabric;

/// <summary>
/// The environment a resource is planned or reconciled in: the one platform it binds, and its
/// policy and parameters.
/// </summary>
/// <param name="Id">The environment's address, <c>/environments/{env}</c>.</param>
/// <param name="Platform">The one platform the environment binds. Platforms are never mixed within an environment.</param>
/// <param name="PlatformApiVersion">The platform contract version the environment serves.</param>
/// <param name="Policy">What the environment allows of the escape hatches.</param>
/// <param name="Parameters">Values the environment supplies to a document, such as a node pool name that differs between staging and production.</param>
public sealed record FabricEnvironment(
    ResourceId Id,
    PlatformName Platform,
    ApiVersion PlatformApiVersion,
    FabricEnvironmentPolicy Policy,
    IReadOnlyDictionary<string, string> Parameters);
