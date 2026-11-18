using System.Collections.Generic;

namespace Assimalign.Cohesion.Fabric;

/// <summary>
/// What a provider validates a resource against.
/// </summary>
/// <param name="TargetPlatforms">The platforms the document targets. At emit, validation runs once for each.</param>
/// <param name="Environment">The environment, at admission; <see langword="null"/> at emit, where no environment exists yet.</param>
public sealed record FabricValidationContext(IReadOnlyList<PlatformName> TargetPlatforms, FabricEnvironment? Environment = null);
