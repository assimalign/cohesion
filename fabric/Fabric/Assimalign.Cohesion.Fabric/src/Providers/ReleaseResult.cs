using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Fabric;

/// <summary>
/// The outcome of releasing a resource before it is deleted.
/// </summary>
/// <param name="Released">Whether everything the resource owns on the platform is gone, so the provider's finalizer can be removed.</param>
/// <param name="Faults">The causes, when <paramref name="Released"/> is <see langword="false"/>.</param>
/// <param name="RequeueAfter">When to try again, if not released.</param>
public sealed record ReleaseResult(bool Released, IReadOnlyList<FabricFault> Faults, TimeSpan? RequeueAfter = null);
