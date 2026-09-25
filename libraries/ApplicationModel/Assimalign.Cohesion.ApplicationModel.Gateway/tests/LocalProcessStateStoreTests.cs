using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel;
using Assimalign.Cohesion.ApplicationModel.Gateway.Internal;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Tests;

public class LocalProcessStateStoreTests
{
    private const string TestHostAssembly = "Assimalign.Cohesion.ApplicationModel.Gateway.TestHost";
    private static readonly TimeSpan _testTimeout = TimeSpan.FromSeconds(30);

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Local process state: stale cleanup preserves a newer registration")]
    public async Task DeleteIfMatchesAsync_NewerRegistrationReplacesExpected_PreservesNewRegistration()
    {
        // Arrange
        string root = Directory.CreateTempSubdirectory("cohesion-process-state-").FullName;
        try
        {
            string stateDirectory = Path.Combine(root, ".cohesion");
            ApplicationName application = ApplicationName.Parse("process-state-tests");
            ResourceName resource = "worker";
            var store = new LocalProcessStateStore(stateDirectory);
            var original = new LocalProcessRegistration
            {
                RegistrationId = Guid.NewGuid(),
                ProcessId = 101,
                StartTimeUtcTicks = 1001,
                ExecutablePath = Path.Combine(root, "original"),
            };
            var replacement = new LocalProcessRegistration
            {
                RegistrationId = Guid.NewGuid(),
                ProcessId = 202,
                StartTimeUtcTicks = 2002,
                ExecutablePath = Path.Combine(root, "replacement"),
                HasProcessGroup = true,
                StopEventName = "replacement-stop",
            };
            await store.SaveAsync(application, resource, original, CancellationToken.None);
            LocalProcessRegistration expected = (await store.LoadAsync(
                application,
                resource,
                CancellationToken.None)).ShouldNotBeNull();
            await store.SaveAsync(application, resource, replacement, CancellationToken.None);

            // Act
            await store.DeleteIfMatchesAsync(
                application,
                resource,
                expected,
                CancellationToken.None);

            // Assert
            LocalProcessRegistration observed = (await store.LoadAsync(
                application,
                resource,
                CancellationToken.None)).ShouldNotBeNull();
            observed.RegistrationId.ShouldBe(replacement.RegistrationId);
            observed.ProcessId.ShouldBe(replacement.ProcessId);
            observed.StartTimeUtcTicks.ShouldBe(replacement.StartTimeUtcTicks);
            observed.ExecutablePath.ShouldBe(replacement.ExecutablePath);
            observed.HasProcessGroup.ShouldBeTrue();
            observed.StopEventName.ShouldBe(replacement.StopEventName);
        }
        finally
        {
            DeleteTestDirectory(root);
        }
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Local process state: application lease excludes another process")]
    public async Task AcquireApplicationLeaseAsync_AnotherProcessHoldsLease_RefusesUntilReleased()
    {
        // Arrange
        string root = Directory.CreateTempSubdirectory("cohesion-process-lease-").FullName;
        string stateDirectory = Path.Combine(root, ".cohesion");
        ApplicationName application = ApplicationName.Parse("process-lease-tests");
        string lockPath = Path.Combine(
            stateDirectory,
            application.ToString(),
            ".state",
            "gateway.lock");
        string readyPath = Path.Combine(root, "lock-ready");
        string releasePath = Path.Combine(root, "lock-release");
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = TestHostPath,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        process.StartInfo.ArgumentList.Add("hold-file-lock");
        process.StartInfo.ArgumentList.Add(lockPath);
        process.StartInfo.ArgumentList.Add(readyPath);
        process.StartInfo.ArgumentList.Add(releasePath);
        bool started = false;

        try
        {
            process.Start().ShouldBeTrue();
            started = true;
            await WaitForFileAsync(readyPath);
            var store = new LocalProcessStateStore(stateDirectory);

            // Act
            InvalidOperationException exception = await Should.ThrowAsync<InvalidOperationException>(
                () => store.AcquireApplicationLeaseAsync(
                    application,
                    $"{application}@local",
                    adopt: false,
                    CancellationToken.None));

            // Assert
            exception.Message.ShouldContain("already supervised", Case.Sensitive);
            File.WriteAllText(releasePath, string.Empty);
            await process.WaitForExitAsync().WaitAsync(_testTimeout);
            process.ExitCode.ShouldBe(0);
            using LocalFileLease lease = await store.AcquireApplicationLeaseAsync(
                application,
                $"{application}@local",
                adopt: false,
                CancellationToken.None);
        }
        finally
        {
            if (started && !process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(_testTimeout);
            }

            DeleteTestDirectory(root);
        }
    }

    private static string TestHostPath => Path.Combine(
        AppContext.BaseDirectory,
        TestHostAssembly + (OperatingSystem.IsWindows() ? ".exe" : string.Empty));

    private static async Task WaitForFileAsync(string path)
    {
        using var cancellation = new CancellationTokenSource(_testTimeout);
        while (!File.Exists(path))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(20), cancellation.Token);
        }
    }

    private static void DeleteTestDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
}
