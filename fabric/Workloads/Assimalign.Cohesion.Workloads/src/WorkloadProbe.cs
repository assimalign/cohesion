using System;

namespace Assimalign.Cohesion.Workloads;

/// <summary>
/// An HTTP probe on one of the workload's ports.
/// </summary>
/// <param name="Port">The name of the probed <see cref="WorkloadPort"/>.</param>
/// <param name="Path">The HTTP path to probe.</param>
/// <param name="Period">How often to probe; the platform's default when <see langword="null"/>.</param>
public sealed record WorkloadProbe(string Port, string Path, TimeSpan? Period = null);
