using System;

namespace Assimalign.Cohesion.Web.Cors;

/// <summary>Contains one coherent policy configuration and its management version.</summary>
/// <param name="Configuration">The detached active policy configuration.</param>
/// <param name="Authority">The authority incarnation; empty denotes unmanaged startup state.</param>
/// <param name="Revision">The applied revision.</param>
/// <param name="ValueDigest">The opaque desired-value digest committed with the configuration.</param>
public sealed record CorsPolicyState(CorsConfiguration Configuration, Guid Authority, long Revision, string ValueDigest);
