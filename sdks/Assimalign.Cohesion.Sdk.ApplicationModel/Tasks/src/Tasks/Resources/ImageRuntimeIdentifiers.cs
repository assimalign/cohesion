using System;
using System.Runtime.InteropServices;

namespace Assimalign.Cohesion.Sdk.ApplicationModel.Tasks;

/// <summary>The Linux runtime identifiers a Cohesion image may target, and their OCI platform mapping.</summary>
internal static class ImageRuntimeIdentifiers
{
    private const string LinuxX64 = "linux-x64";
    private const string LinuxMuslX64 = "linux-musl-x64";
    private const string LinuxArm64 = "linux-arm64";
    private const string LinuxMuslArm64 = "linux-musl-arm64";

    /// <summary>The text listed in the rejection diagnostic, in support order.</summary>
    internal const string SupportedList = LinuxX64 + ", " + LinuxArm64 + ", or the opt-in " + LinuxMuslX64 + " / " + LinuxMuslArm64;

    /// <summary>Returns whether the runtime identifier names a supported image target.</summary>
    internal static bool IsSupported(string runtimeIdentifier) =>
        runtimeIdentifier is LinuxX64 or LinuxMuslX64 or LinuxArm64 or LinuxMuslArm64;

    /// <summary>Returns whether the runtime identifier selects the musl libc (Alpine) base.</summary>
    internal static bool IsMusl(string runtimeIdentifier) =>
        runtimeIdentifier is LinuxMuslX64 or LinuxMuslArm64;

    /// <summary>Returns the processor architecture a supported runtime identifier targets.</summary>
    internal static Architecture GetArchitecture(string runtimeIdentifier) => runtimeIdentifier switch
    {
        LinuxX64 or LinuxMuslX64 => Architecture.X64,
        LinuxArm64 or LinuxMuslArm64 => Architecture.Arm64,
        _ => throw new ArgumentOutOfRangeException(nameof(runtimeIdentifier), runtimeIdentifier, "Unsupported image runtime identifier."),
    };

    /// <summary>Returns the lowercase OCI platform recorded in the cohesion/image/v1 index.</summary>
    /// <remarks>The libc variant is carried by the base-image identity, not by the platform string.</remarks>
    internal static string GetOciPlatform(string runtimeIdentifier) => GetArchitecture(runtimeIdentifier) switch
    {
        Architecture.Arm64 => "linux/arm64",
        _ => "linux/amd64",
    };
}
