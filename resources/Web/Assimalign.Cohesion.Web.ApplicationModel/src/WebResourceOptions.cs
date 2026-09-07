using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.Web.ApplicationModel;

/// <summary>
/// Provides deployer-owned planning overrides for a <see cref="WebResource"/>.
/// </summary>
/// <remarks>
/// <see cref="ResourceOptions.Replicas"/> overrides the manifest replica count.
/// Public exposure remains a build-produced manifest fact in realization-plan v1.
/// </remarks>
public sealed class WebResourceOptions : ResourceOptions
{
}
