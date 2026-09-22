using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace Assimalign.Cohesion.Sdk.ApplicationModel.Tasks;

/// <summary>Validates container inputs and selects the configuration and host-capability route.</summary>
// Deviates from the repo interface-first rule per work-item requirements: MSBuild discovers public Task subclasses.
public sealed class CohesionResolveImagePublish : Task
{
    /// <summary>Gets or sets the build configuration.</summary>
    public string Configuration { get; set; } = string.Empty;
    /// <summary>Gets or sets auto, true, or false.</summary>
    public string Aot { get; set; } = "auto";
    /// <summary>Gets or sets the owning project path.</summary>
    [Required]
    public string ProjectPath { get; set; } = string.Empty;
    /// <summary>Gets or sets the selected Linux runtime identifier.</summary>
    public string RuntimeIdentifier { get; set; } = "linux-x64";
    /// <summary>Gets or sets the repository.</summary>
    public string Repository { get; set; } = string.Empty;
    /// <summary>Gets or sets the configured registry and optional organization prefix.</summary>
    public string Registry { get; set; } = string.Empty;
    /// <summary>Gets or sets whether this build pushes its image.</summary>
    public bool Push { get; set; }
    /// <summary>Gets or sets the image index destination.</summary>
    [Required]
    public string ImagePath { get; set; } = string.Empty;
    /// <summary>Gets or sets the archive destination.</summary>
    public string ArchivePath { get; set; } = string.Empty;
    /// <summary>Gets or sets the requested base or auto.</summary>
    public string BaseImage { get; set; } = "auto";
    /// <summary>Gets or sets the private CLI in-container request.</summary>
    public bool InContainer { get; set; }
    /// <summary>Gets the NativeAOT selection.</summary>
    [Output]
    public bool PublishAot { get; private set; }
    /// <summary>Gets the concrete base image.</summary>
    [Output]
    public string ResolvedBaseImage { get; private set; } = string.Empty;
    /// <summary>Gets the authority for the registry sink, or empty for archives.</summary>
    [Output]
    public string RegistryAuthority { get; private set; } = string.Empty;
    /// <summary>Gets whether the caller must forward through cohesion publish --in-container.</summary>
    [Output]
    public bool ForwardToContainer { get; private set; }

    /// <inheritdoc />
    public override bool Execute()
    {
        try
        {
            if (Aot is not ("auto" or "true" or "false"))
            {
                throw new InvalidDataException("CohesionImageAot must be auto, true, or false.");
            }

            if (Configuration is not ("Debug" or "Release"))
            {
                throw new InvalidDataException("Cohesion image publishing requires Debug (Development) or Release configuration.");
            }

            if (Configuration == "Release" && Aot == "false")
            {
                throw new InvalidDataException("CohesionImageAot=false in Release deviates from T12/O11: Release images must be NativeAOT. Use Debug for a self-contained JIT image.");
            }

            if (!ImageRuntimeIdentifiers.IsSupported(RuntimeIdentifier))
            {
                throw new InvalidDataException($"Cohesion images support {ImageRuntimeIdentifiers.SupportedList} RuntimeIdentifier.");
            }

            if (string.IsNullOrWhiteSpace(Repository))
            {
                throw new InvalidDataException("Set CohesionContainerRepository or CohesionOrganization so the container repository can be formed.");
            }

            ImageIndexFile.Repository(Repository);
            if (Push)
            {
                RegistryAuthority = Registry.Split('/')[0];
                ImageIndexFile.Registry(RegistryAuthority);
                if (Registry.Contains('/'))
                {
                    string prefix = Registry[(Registry.IndexOf('/') + 1)..];
                    ImageIndexFile.Repository(prefix);
                    if (!Repository.StartsWith(prefix + "/", StringComparison.Ordinal))
                    {
                        throw new InvalidDataException("CohesionContainerRegistry's path prefix must already prefix CohesionContainerRepository; it is not part of the registry authority.");
                    }
                }
            }
            else
            {
                ImageIndexFile.RelativeArchive(ImagePath, ArchivePath);
            }

            ResolvedBaseImage = BaseImage is "" or "auto"
                ? "mcr.microsoft.com/dotnet/runtime-deps:10.0" + (ImageRuntimeIdentifiers.IsMusl(RuntimeIdentifier) ? "-alpine" : "")
                : BaseImage;
            PublishAot = Aot == "true" || Configuration == "Release";
            if (!PublishAot && !InContainer)
            {
                return true;
            }

            // NativeAOT cannot cross-compile across operating systems or architectures: only a Linux
            // host of the image's own architecture with a reachable clang can build the payload directly.
            bool nativeHost = OperatingSystem.IsLinux()
                && RuntimeInformation.ProcessArchitecture == ImageRuntimeIdentifiers.GetArchitecture(RuntimeIdentifier);
            if (nativeHost && !InContainer && Probe("clang", ["--version"], out _))
            {
                return true;
            }

            string reason = nativeHost ? $"the {RuntimeIdentifier} host has no reachable clang toolchain" : $"host '{RuntimeInformation.RuntimeIdentifier}' cannot build NativeAOT for '{RuntimeIdentifier}'";
            if (!Probe("docker", ["info", "--format", "{{.OSType}}"], out string output) || output.Trim() != "linux")
            {
                return Unavailable($"{reason}; no reachable Linux Docker daemon is available.");
            }

            if (InContainer)
            {
                return Unavailable("a Linux Docker daemon is reachable, but the in-container build image, mounts, and command contract are not specified (item 15 / #955). No Release image was produced.");
            }

            ForwardToContainer = true;
            return true;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            Log.LogError(exception.Message);
            return false;
        }
    }

    private bool Unavailable(string reason)
    {
        Log.LogError(null, Configuration == "Release" ? "COHSDK003" : null, null, ProjectPath, 0, 0, 0, 0, $"NativeAOT image build is unavailable: {reason}");
        return false;
    }

    private static bool Probe(string executable, string[] arguments, out string output)
    {
        output = string.Empty;
        try
        {
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string argument in arguments)
            {
                start.ArgumentList.Add(argument);
            }

            using Process process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(10000))
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
                return false;
            }
            output = stdout.GetAwaiter().GetResult();
            stderr.GetAwaiter().GetResult();
            return process.ExitCode == 0;
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or IOException)
        {
            return false;
        }
    }
}
