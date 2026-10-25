using System;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

/// <summary>Reports one platform process's observed state revision.</summary>
/// <param name="HostId">The platform host identity.</param>
/// <param name="Generation">The platform process generation.</param>
/// <param name="ProcessStartId">The UUID returned by the authenticated runtime handshake.</param>
/// <param name="AppliedRevision">The actual applied revision, when known.</param>
/// <param name="ErrorCode">A stable error code, without secret data.</param>
public sealed record ResourceStateInstanceProgress(string HostId, string Generation, Guid ProcessStartId, long? AppliedRevision, string? ErrorCode);
