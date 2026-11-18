using Assimalign.Cohesion.Fabric.InProcess.Internal;
using Assimalign.Cohesion.Workloads;

namespace Assimalign.Cohesion.Fabric.InProcess;

/// <summary>
/// The in-process platform: runs an application's programs inside one process, for local
/// development. It implements the same contract as every other platform, which keeps the
/// contract honest.
/// </summary>
/// <remarks>
/// <para>
/// This build registers the platform's fault classifier and its typed workload extension, and
/// publishes its workload capabilities. It does not run programs yet: that needs the program
/// bridge that turns a program reference into an entry point the platform can call, which
/// depends on the v2 design's decision 9 (framework-owned tier entries through SDK-generated
/// programs), and a way for a platform to register its area drivers, which the design leaves open.
/// </para>
/// </remarks>
public sealed class InProcessPlatform : FabricPlatform
{
    /// <summary>
    /// Gets the platform's name, <c>inprocess</c>.
    /// </summary>
    public static PlatformName PlatformName { get; } = new("inprocess");

    /// <summary>
    /// Gets the platform contract version this build serves.
    /// </summary>
    public static ApiVersion PlatformContractVersion { get; } = new("2026-12-01");

    /// <summary>
    /// Gets what the platform does with each portable workload field: programs only, one
    /// replica, no isolation, and mounts, state, and probes emulated.
    /// </summary>
    public static WorkloadCapabilities WorkloadCapabilities { get; } = new(
        ProgramSource: Support.Native,
        ImageSource: Support.Unsupported,
        MaxReplicas: 1,
        PersistentMounts: Support.Emulated("A mount is a directory under the application's content root."),
        Stateful: Support.Emulated("One instance; its state is a local directory that survives restarts."),
        OneShotTasks: Support.Native,
        Probes: Support.Emulated("Probed over loopback by the platform, not by an orchestrator."),
        Isolation: Isolation.None);

    /// <inheritdoc />
    public override PlatformName Name => PlatformName;

    /// <inheritdoc />
    public override ApiVersion ContractVersion => PlatformContractVersion;

    /// <inheritdoc />
    public override void Configure(IFabricPlatformBuilder platform)
    {
        System.ArgumentNullException.ThrowIfNull(platform);

        platform
            .AddFaultClassifier(new InProcessFaultClassifier())
            .AddExtension(new InProcessWorkloadExtension());
    }
}
