namespace Assimalign.Cohesion.Fabric.InProcess;

/// <summary>
/// The in-process platform's typed workload extension (tier T1), written at
/// <c>spec.platforms.inprocess</c> on a workload.
/// </summary>
public sealed record InProcessWorkloadSettings
{
    /// <summary>
    /// Gets the content root the program runs with, relative to the application; the program's
    /// own directory when <see langword="null"/>.
    /// </summary>
    public string? ContentRoot { get; init; }
}
