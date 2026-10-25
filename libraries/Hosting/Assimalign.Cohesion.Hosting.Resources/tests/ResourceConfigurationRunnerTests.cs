using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting.Resources;
using Assimalign.Cohesion.Hosting.Resources.Internal;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Hosting.Resources.Tests;

public sealed class ResourceConfigurationRunnerTests : IDisposable
{
    private const string DisplayPrefix = "Cohesion Test [Hosting.Resources] - Configuration runner: ";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "cohesion-runner-" + Guid.NewGuid().ToString("N"));

    public ResourceConfigurationRunnerTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
    }

    [Fact(DisplayName = DisplayPrefix + "The record is applied before the host runs")]
    public async Task RunAsync_WithRecordDirectory_AppliesBeforeTheHostRuns()
    {
        // Arrange
        Write("deployment.json", """{"Test:Policy":{"origins":["https://b.example"]}}""");
        (ResourceControlPlane plane, RecordingSectionTarget<TestPolicyOptions> policy) = CreatePlane();
        string[]? originsAtStart = null;
        var inner = new RecordingHostRunner(static _ => Task.CompletedTask, () => originsAtStart = policy.Current.Origins);
        var runner = new ResourceConfigurationRunner(inner, plane, _directory, TimeSpan.FromMilliseconds(20));

        // Act
        await runner.RunAsync(new TestHostRun(new TestHost(new TestHostOptions())), CancellationToken.None);

        // Assert
        originsAtStart.ShouldBe(["https://b.example"]);
        plane.ConfigurationStatus.Sections[0].State.ShouldBe(ResourceConfigurationState.Applied);
        inner.Runs.ShouldBe(1);
    }

    [Fact(DisplayName = DisplayPrefix + "While the host runs, a changed record is applied without a gateway")]
    public async Task RunAsync_WhileRunning_FollowsTheDirectory()
    {
        // Arrange
        Write("deployment.json", """{"Test:Policy":{"origins":["https://b.example"]}}""");
        (ResourceControlPlane plane, RecordingSectionTarget<TestPolicyOptions> policy) = CreatePlane();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var stopped = new TaskCompletionSource();
        var inner = new RecordingHostRunner(token => stopped.Task.WaitAsync(token));
        var runner = new ResourceConfigurationRunner(inner, plane, _directory, TimeSpan.FromMilliseconds(20));

        // Act
        Task running = runner.RunAsync(new TestHostRun(new TestHost(new TestHostOptions())), timeout.Token);
        Write("override.json", """{"Test:Policy":{"origins":["https://b.example","https://partner.example"]}}""");
        while (policy.Current.Origins.Length != 2)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(20), timeout.Token);
        }

        stopped.SetResult();
        await running;

        // Assert
        policy.Current.Origins.ShouldBe(["https://b.example", "https://partner.example"]);
        plane.ConfigurationStatus.RecordDigest.ShouldBe(ResourceConfigurationRecord.Load(_directory).Digest);
    }

    [Fact(DisplayName = DisplayPrefix + "Without a record directory the host runs on its baseline")]
    public async Task RunAsync_MissingDirectory_RunsOnTheBaseline()
    {
        // Arrange
        (ResourceControlPlane plane, RecordingSectionTarget<TestPolicyOptions> policy) = CreatePlane();
        var inner = new RecordingHostRunner(static _ => Task.CompletedTask);
        var runner = new ResourceConfigurationRunner(inner, plane, Path.Combine(_directory, "absent"), TimeSpan.FromMilliseconds(20));

        // Act
        await runner.RunAsync(new TestHostRun(new TestHost(new TestHostOptions())), CancellationToken.None);

        // Assert
        inner.Runs.ShouldBe(1);
        policy.Applies.ShouldBe(0);
        plane.ConfigurationStatus.ShouldBeSameAs(ResourceConfigurationStatus.Empty);
    }

    [Fact(DisplayName = DisplayPrefix + "HostBuilt installs the runner when the gateway delivers a record directory")]
    public void HostBuilt_WithConfigDirectory_InstallsTheRunner()
    {
        // Arrange
        ResourceContext context = ResourceContext.FromEnvironment(new Dictionary<string, string?>
        {
            [AppEnvironment.Variables.ConfigDirectory] = _directory,
        });
        using IDisposable scope = ResourceRuntime.CreateScope(context);
        var host = new TestHost(new TestHostOptions());

        // Act
        ResourceRuntime.HostBuilt(host, ResourceControlPlane.Create());

        // Assert
        ResourceConfigurationRunner runner = host.Context.Runner.ShouldBeOfType<ResourceConfigurationRunner>();
        runner.Directory.ShouldBe(_directory);
    }

    private static (ResourceControlPlane Plane, RecordingSectionTarget<TestPolicyOptions> Policy) CreatePlane()
    {
        var baseline = new TestPolicyOptions { Origins = ["https://a.example"] };
        var policy = new RecordingSectionTarget<TestPolicyOptions>(baseline);
        ResourceControlPlane plane = ResourceControlPlane.Create();
        plane.AddConfiguration(ResourceConfigurationSection.Create(
            "Test:Policy",
            TestSurfaceJsonContext.Default.TestPolicyOptions,
            ResourceConfigurationMutability.Live,
            baseline,
            policy.Apply));
        return (plane, policy);
    }

    private void Write(string name, string content)
    {
        // Replace atomically, as a gateway does, so a poll never reads a half-written layer.
        string target = Path.Combine(_directory, name);
        string temporary = target + ".tmp";
        File.WriteAllText(temporary, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temporary, target, overwrite: true);
    }
}
