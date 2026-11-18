using System;

namespace Assimalign.Cohesion.Fabric;

/// <summary>
/// The operation a reconcile, release, or action runs under: the verdict on one attempt.
/// </summary>
/// <param name="Id">The operation's address, <c>/environments/{env}/operations/{operation}</c>.</param>
/// <param name="Generation">The spec generation the operation applies.</param>
/// <param name="StartedAt">When the operation started.</param>
public sealed record FabricOperation(ResourceId Id, long Generation, DateTimeOffset StartedAt);
