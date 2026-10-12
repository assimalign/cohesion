using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel.Gateway.Internal;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Tests;

[Collection(LocalGatewayConsoleCollection.Name)]
public class LocalProbePathTests
{
    [WindowsProcessTheory(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Exec probe: quoted cmd keeps the baseline exit code")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_ShortCmdPath_MatchesProcessStart(bool quoted)
    {
        // Arrange
        string command = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        command = quoted ? $"\"{command}\"" : command;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var runner = new LocalProbeRunner(new LocalGatewayOptions());
        var configuration = CreateConfiguration(Path.Combine(AppContext.BaseDirectory, "host.exe"), new Dictionary<string, string>());

        // Act
        ProbeAttemptResult result = await runner.RunAsync(ProbeSpec.Exec(command, "/c", "exit", "0"), configuration, cancellation.Token);

        // Assert
        result.Succeeded.ShouldBeTrue(result.Detail);
    }

    [WindowsProcessTheory(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Exec probe: keeps file, arguments and working directory for each short command form")]
    [InlineData("absolute", false)]
    [InlineData("absolute", true)]
    [InlineData("relative", false)]
    [InlineData("relative", true)]
    [InlineData("bare", false)]
    [InlineData("bare", true)]
    public async Task RunAsync_ShortCommandForms_MatchesProcessStart(string form, bool quoted)
    {
        // Arrange
        string root = Directory.CreateTempSubdirectory("cohesion-probe-path-").FullName;
        string? originalPath = Environment.GetEnvironmentVariable("PATH");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            string directory = Directory.CreateDirectory(Path.Combine(root, "host with spaces")).FullName;
            foreach (string source in Directory.EnumerateFiles(AppContext.BaseDirectory))
            {
                File.Copy(source, Path.Combine(directory, Path.GetFileName(source)));
            }

            string executable = Path.Combine(directory, $"probe-{Guid.NewGuid():N}.exe");
            File.Copy(Path.Combine(directory, "Assimalign.Cohesion.ApplicationModel.Gateway.TestHost.exe"), executable);
            Environment.SetEnvironmentVariable("PATH", directory + Path.PathSeparator + originalPath);
            string command = form switch
            {
                "relative" => Path.GetRelativePath(Environment.CurrentDirectory, executable),
                "bare" => Path.GetFileName(executable),
                _ => executable,
            };
            string artifact = command;
            string workingDirectory = Path.GetDirectoryName(artifact) ?? Environment.CurrentDirectory;
            command = quoted ? $"\"{command}\"" : command;
            artifact = quoted ? $"\"{artifact}\"" : artifact;
            string capture = Path.Combine(root, "capture.txt");
            string[] arguments = ["capture-launch", "", "two words", "a\"b", @"trailing\"];
            var baseline = new ProcessStartInfo(command)
            {
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
            };
            foreach (string argument in arguments)
            {
                baseline.ArgumentList.Add(argument);
            }

            baseline.Environment["TEST_LAUNCH_CAPTURE"] = capture;
            using (Process process = Process.Start(baseline)!)
            {
                try
                {
                    await process.WaitForExitAsync(cancellation.Token);
                    process.ExitCode.ShouldBe(0);
                }
                finally
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        await process.WaitForExitAsync(CancellationToken.None);
                    }
                }
            }

            string[] expected = File.ReadAllLines(capture);
            File.Delete(capture);
            var runner = new LocalProbeRunner(new LocalGatewayOptions { ProbeTimeout = TimeSpan.FromSeconds(10) });
            var configuration = CreateConfiguration(artifact, new Dictionary<string, string> { ["TEST_LAUNCH_CAPTURE"] = capture });

            // Act
            ProbeAttemptResult result = await runner.RunAsync(ProbeSpec.Exec(command, arguments), configuration, cancellation.Token);

            // Assert
            result.Succeeded.ShouldBeTrue(result.Detail);
            File.ReadAllLines(capture).ShouldBe(expected);
            expected[0].ShouldBe(executable);
            expected[1].ShouldBe(form == "bare" ? Environment.CurrentDirectory : directory);
            expected[2..].ShouldBe(arguments);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
            Directory.Delete(root, recursive: true);
        }
    }

    [WindowsProcessTheory(true, DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Exec probe: quoted and unquoted long commands retain arguments and current directory")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_LongCommandAndQuotedHost_LaunchesSameFile(bool quoted)
    {
        // Arrange
        using var directory = new LongPathTestDirectory();
        directory.CreateExecutable(copyHost: true);
        string capture = Path.Combine(directory.Root, "capture.txt");
        string command = quoted ? $"\"{directory.ExecutablePath}\"" : directory.ExecutablePath;
        var runner = new LocalProbeRunner(new LocalGatewayOptions { ProbeTimeout = TimeSpan.FromSeconds(10) });
        var configuration = CreateConfiguration(command, new Dictionary<string, string> { ["TEST_LAUNCH_CAPTURE"] = capture });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        // Act
        ProbeAttemptResult result = await runner.RunAsync(ProbeSpec.Exec(command, "capture-launch", "two words", ""), configuration, cancellation.Token);

        // Assert
        result.Succeeded.ShouldBeTrue(result.Detail);
        string[] observed = File.ReadAllLines(capture);
        LocalProcessPath.TryGetShortPath(observed[0]).ShouldBe(LocalProcessPath.TryGetShortPath(directory.ExecutablePath));
        LocalProcessPath.TryGetShortPath(observed[1]).ShouldBe(LocalProcessPath.TryGetShortPath(directory.HostDirectory));
        observed[2..].ShouldBe(["capture-launch", "two words", ""]);
    }

    private static LocalResourceConfiguration CreateConfiguration(string executable, IReadOnlyDictionary<string, string> environment)
    {
        IApplicationResource resource = new TestResource("host");
        return new LocalResourceConfiguration("probe-path", new InMemoryResourceStateManager(), resource,
            new ExecutableArtifact(resource.Id, executable), environment, [], null, null, null, null,
            false, RestartPolicy.Never, TimeSpan.FromSeconds(1), false);
    }
}
