using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Sdk.ApplicationModel.Tests;

/// <summary>Verifies the shipped ApplicationModel SDK evaluates under Visual Studio MSBuild.</summary>
public sealed class VisualStudioMsBuildEvaluationTests
{
    /// <summary>Restores an enabled area consumer with Visual Studio's .NET Framework MSBuild.</summary>
    /// <returns>A task representing the restore.</returns>
    [Fact(DisplayName = "Cohesion Test [Sdk.ApplicationModel] - Visual Studio MSBuild evaluates an enabled resource")]
    public async Task Restore_EnabledResourceWithVisualStudioMsBuild_ShouldSucceed()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // Arrange
        string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string vswhere = Path.Combine(programFilesX86, "Microsoft Visual Studio", "Installer", "vswhere.exe");
        File.Exists(vswhere).ShouldBeTrue($"Expected Visual Studio locator '{vswhere}'.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        string msbuild = await FindMsBuildAsync(vswhere, timeout.Token);
        using ConsumerWorkspace workspace = ConsumerWorkspace.Create("MinimalResource");

        // Act
        DotNetBuildResult result = await RunMsBuildAsync(msbuild, workspace, timeout.Token);

        // Assert
        result.ExitCode.ShouldBe(0, result.Output);
    }

    private static async Task<string> FindMsBuildAsync(string vswhere, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = vswhere,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-latest");
        startInfo.ArgumentList.Add("-prerelease");
        startInfo.ArgumentList.Add("-find");
        startInfo.ArgumentList.Add(@"MSBuild\**\Bin\MSBuild.exe");

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start vswhere.");
        string output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        string error = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        process.ExitCode.ShouldBe(0, output + Environment.NewLine + error);
        string? path = output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(File.Exists);
        path.ShouldNotBeNullOrWhiteSpace("Visual Studio MSBuild was not found.");
        return path!;
    }

    private static async Task<DotNetBuildResult> RunMsBuildAsync(
        string msbuild,
        ConsumerWorkspace workspace,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = msbuild,
            WorkingDirectory = workspace.RootDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(workspace.ProjectFile("MinimalResource"));
        startInfo.ArgumentList.Add("-t:Restore");
        startInfo.ArgumentList.Add("-nologo");
        startInfo.ArgumentList.Add("-verbosity:minimal");
        startInfo.Environment["NUGET_PACKAGES"] = Path.Combine(workspace.RootDirectory, ".nuget", "packages");
        startInfo.Environment["MSBUILDDISABLENODEREUSE"] = "1";

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start Visual Studio MSBuild.");
        Task<string> output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> error = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return new DotNetBuildResult(process.ExitCode, await output, await error);
    }
}
