namespace Assimalign.Cohesion.Workloads;

/// <summary>
/// A port a workload listens on. An exposure in the Networking area targets it by name.
/// </summary>
/// <param name="Name">The port's name, such as <c>http</c>.</param>
/// <param name="Port">The port number inside the workload.</param>
public sealed record WorkloadPort(string Name, int Port);
