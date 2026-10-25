using System.Collections.Generic;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Internal;

/// <summary>What one instance reported at its configuration status route, or why it could not be read.</summary>
/// <param name="Address">The instance's control-plane address, or null for an in-process instance.</param>
/// <param name="Instance">The platform instance identity the instance reported, if any.</param>
/// <param name="Reachable">Whether the instance answered.</param>
/// <param name="Error">Why the instance could not be read, when it could not.</param>
/// <param name="SurfaceDigest">The digest of the runtime surface the instance runs.</param>
/// <param name="RecordDigest">The digest of the record the instance applied, or null before it applied one.</param>
/// <param name="Sections">Each section's outcome.</param>
internal sealed record InstanceStatusReport(
    string? Address,
    string? Instance,
    bool Reachable,
    string? Error,
    string? SurfaceDigest,
    string? RecordDigest,
    IReadOnlyList<InstanceSectionReport> Sections);
