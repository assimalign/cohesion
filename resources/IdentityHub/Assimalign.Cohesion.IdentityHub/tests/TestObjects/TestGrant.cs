using System;

namespace Assimalign.Cohesion.IdentityHub.Tests;

/// <summary>
/// A hub value shaped like a device authorization: found by its key and by a unique user code,
/// listed by client, and gone after its expiry.
/// </summary>
internal sealed record TestGrant(
    string Code,
    string UserCode,
    string ClientId,
    string? Subject,
    DateTimeOffset ExpiresAt);
