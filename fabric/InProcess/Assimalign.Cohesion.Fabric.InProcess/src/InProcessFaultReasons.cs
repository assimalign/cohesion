namespace Assimalign.Cohesion.Fabric.InProcess;

/// <summary>
/// The reasons the in-process platform reports in a <see cref="PlatformDetail"/>.
/// </summary>
public static class InProcessFaultReasons
{
    /// <summary>
    /// The program's entry point threw.
    /// </summary>
    public const string EntryPointThrew = "EntryPointThrew";

    /// <summary>
    /// A long-running program's entry point returned.
    /// </summary>
    public const string EntryPointExited = "EntryPointExited";

    /// <summary>
    /// The workload names a program the process does not contain.
    /// </summary>
    public const string ProgramMissing = "ProgramMissing";
}
