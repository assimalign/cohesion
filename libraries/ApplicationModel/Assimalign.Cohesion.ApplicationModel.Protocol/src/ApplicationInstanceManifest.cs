using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// What a resource instance publishes about itself when its host starts: its identity, the
/// deployment that started it, where its gateway reaches its control plane, and the actions it
/// exposes there.
/// </summary>
/// <param name="Provider">The resource's provider.</param>
/// <param name="Name">The resource's name.</param>
/// <param name="Key">The instance's key.</param>
/// <param name="Deployment">
/// The deployment that started the instance, as its environment named it; <see langword="null"/> for
/// an instance no gateway deployed. A gateway counts a deployment ready only once an instance that
/// repeats its id publishes.
/// </param>
/// <param name="ControlPlaneEndpoint">
/// Where the gateway reaches the instance's control plane, as the platform's client that published
/// the manifest names it; <see langword="null"/> for an instance the gateway cannot call. Its form is
/// the platform's: the gateway hands it to the platform's instance client to run an operator's action
/// on the instance and to restore a write made on another instance. An instance it cannot reach this
/// way receives the state when it next publishes.
/// </param>
/// <param name="Actions">The instance's actions, ordered by name.</param>
public sealed record ApplicationInstanceManifest(
    ProviderName Provider,
    ResourceName Name,
    InstanceKey Key,
    DeploymentId? Deployment,
    Uri? ControlPlaneEndpoint,
    IReadOnlyList<ApplicationActionDescriptor> Actions);
