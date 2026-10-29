using System.Collections.Generic;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

/// <summary>
/// One resource as its gateway recorded it.
/// </summary>
/// <param name="Provider">The resource's provider.</param>
/// <param name="Name">The resource's name.</param>
/// <param name="Revision">
/// The revision deployed: a fingerprint of the resource's definition in the model, followed by its
/// infrastructure provider's revision.
/// </param>
/// <param name="State">The snapshots recorded since that revision was deployed, one per action.</param>
/// <param name="Instances">The manifests of the instances registered with the gateway.</param>
/// <param name="Outputs">The values the platform reported for the deployed resource, by name.</param>
public sealed record ApplicationResourceRecord(
    ProviderName Provider,
    ResourceName Name,
    string Revision,
    IReadOnlyList<ApplicationActionSnapshot> State,
    IReadOnlyList<ApplicationInstanceManifest> Instances,
    IReadOnlyDictionary<OutputName, string> Outputs);
