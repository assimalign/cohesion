using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// What a gateway answers a publication with: the state the instance must restore before it serves,
/// and how long the gateway keeps the instance registered without a renewal.
/// </summary>
/// <remarks>
/// In JSON it reads <c>{ "snapshots": [ { "action": "cors", "value": … } ], "lease": "00:00:30" }</c>.
/// </remarks>
/// <param name="Snapshots">The snapshots to restore, at most one per action; empty for none.</param>
/// <param name="Lease">
/// How long the registration lasts without a renewal (<see cref="IResourceManifestPublisher.RenewAsync"/>),
/// or <see langword="null"/> when it does not expire.
/// </param>
public sealed record ResourcePublication(IReadOnlyList<ResourceStateSnapshot> Snapshots, TimeSpan? Lease);
