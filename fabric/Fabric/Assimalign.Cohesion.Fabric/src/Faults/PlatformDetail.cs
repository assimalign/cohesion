using System.Collections.Generic;

namespace Assimalign.Cohesion.Fabric;

/// <summary>
/// A platform's own account of a cause, kept verbatim.
/// </summary>
/// <param name="Platform">The platform that reported it.</param>
/// <param name="Reason">The platform's reason, such as <c>ImagePullBackOff</c>.</param>
/// <param name="Object">The platform object the reason is about, such as <c>v1/Pod/acme-identity/identity-store-0</c>.</param>
/// <param name="Message">The platform's message.</param>
/// <param name="Events">The platform events behind the reason, carried forward while the reason is unchanged.</param>
/// <param name="Output">A bounded tail of the program's own output, after redaction.</param>
public sealed record PlatformDetail(
    PlatformName Platform,
    string Reason,
    string? Object,
    string? Message,
    IReadOnlyList<PlatformEvent> Events,
    string? Output);
