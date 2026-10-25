using System;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

/// <summary>Identifies a trusted platform target; the protocol handshake establishes its process UUID.</summary>
/// <param name="HostId">The platform's immutable host identity.</param>
/// <param name="Generation">The platform process/container generation.</param>
/// <param name="Address">The individually addressable management base URI.</param>
/// <param name="IsTerminating">Whether new state delivery must avoid this target.</param>
/// <param name="ServerName">The resource TLS server identity when the direct address is a platform IP.</param>
/// <param name="VerifyHostIdentity">Whether the authenticated handshake must match the platform-injected host identity.</param>
public sealed record ResourceManagementTarget(string HostId, string Generation, Uri Address, bool IsTerminating = false, string? ServerName = null, bool VerifyHostIdentity = false);
