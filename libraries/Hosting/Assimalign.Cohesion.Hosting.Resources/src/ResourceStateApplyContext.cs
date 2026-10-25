using System;

namespace Assimalign.Cohesion.Hosting.Resources;

/// <summary>Carries gateway-issued revision and target binding for one state application.</summary>
/// <param name="ResourceIncarnation">The gateway's current logical deployment identity.</param>
/// <param name="TargetProcess">The process identity established by the runtime handshake.</param>
/// <param name="Revision">The durable gateway-issued revision.</param>
/// <param name="ValueDigest">The digest of the desired value.</param>
/// <param name="Fence">The current coordinator/backend fencing token.</param>
public sealed record ResourceStateApplyContext(
    Guid ResourceIncarnation, Guid TargetProcess, long Revision, string ValueDigest, long Fence);
