using System;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel.Gateway.Internal;
using Assimalign.Cohesion.Testing;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Tests;

/// <summary>
/// Event sources are process-wide, so the tests that listen to one run alone.
/// </summary>
[CollectionDefinition(nameof(ApplicationGatewayEventSourceTests), DisableParallelization = true)]
public sealed class ApplicationGatewayEventSourceCollection
{
}

/// <summary>
/// The gateway's event source: one event per transition of a run and of each resource's step. Each
/// test runs a gateway with a name of its own and reads only that gateway's events.
/// </summary>
[Collection(nameof(ApplicationGatewayEventSourceTests))]
public sealed class ApplicationGatewayEventSourceTests
{
    private const string DisplayPrefix = "Cohesion Test [ApplicationModel] - ApplicationGatewayEventSource: ";

    [Fact(DisplayName = DisplayPrefix + "The source is named after its assembly")]
    public void GetName_ShouldBeTheAssemblyName()
    {
        // Act
        string name = EventSource.GetName(typeof(ApplicationGatewayEventSource));

        // Assert
        name.ShouldBe("Assimalign.Cohesion.ApplicationModel.Gateway");
    }

    [Fact(DisplayName = DisplayPrefix + "The source generates a manifest in strict mode")]
    [UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode", Justification = "Test-only manifest validation reflects over the event source; test assemblies are not trimmed.")]
    [UnconditionalSuppressMessage("Trimming", "IL2111:DynamicallyAccessedMembers", Justification = "Test-only manifest validation reflects over the event source; test assemblies are not trimmed.")]
    public void GenerateManifest_StrictMode_ShouldSucceed()
    {
        // Act
        string? manifest = EventSource.GenerateManifest(typeof(ApplicationGatewayEventSource), assemblyPathToIncludeInManifest: null, EventManifestOptions.Strict);

        // Assert
        manifest.ShouldNotBeNull();
    }

    [Fact(DisplayName = DisplayPrefix + "A run reports each resource's plan, deployment and readiness, then its start and stop")]
    public async Task RunAsync_ShouldReportEachStep()
    {
        // Arrange
        using var recorder = new EventSourceRecorder(ApplicationGatewayEventSource.Log, EventLevel.Informational);
        var infrastructure = new TestInfrastructure();

        // Act
        await using (await RunningGateway.StartAsync(infrastructure, new InMemoryApplicationGatewayStore(), CreateModel("events-run")))
        {
        }

        // Assert
        Describe(recorder, "events-run").ShouldBe([
            "RunStarting 1",
            "ResourcePlanned Database.Sql/db Create",
            "ResourceDeploying Database.Sql/db",
            "ResourceReady Database.Sql/db",
            "ResourcePlanned Web/api Create",
            "ResourceDeploying Web/api",
            "ResourceReady Web/api",
            "RunStarted 2",
            "RunStopped",
        ]);
        recorder.Events.ShouldNotContain(written => written.EventId == 0);
    }

    [Fact(DisplayName = DisplayPrefix + "A run reports a missed window, what it skipped, and its failure")]
    public async Task RunAsync_WhenAResourceMissesItsWindow_ShouldReportMissedSkippedAndFailed()
    {
        // Arrange
        using var recorder = new EventSourceRecorder(ApplicationGatewayEventSource.Log, EventLevel.Informational);
        var infrastructure = new TestInfrastructure();
        infrastructure.Silent.Add("db");
        ApplicationGateway gateway = RunningGateway.Create(infrastructure, new InMemoryApplicationGatewayStore(), CreateModel("events-miss"), TimeSpan.FromMilliseconds(200));

        // Act
        await Should.ThrowAsync<ApplicationGatewayException>(() => gateway.RunAsync());

        // Assert
        Describe(recorder, "events-miss").ShouldBe([
            "RunStarting 1",
            "ResourcePlanned Database.Sql/db Create",
            "ResourceDeploying Database.Sql/db",
            "ResourceMissed Database.Sql/db",
            "ResourceSkipped Web/api",
            "RunFailed Assimalign.Cohesion.ApplicationModel.Gateway.ApplicationGatewayException",
        ]);
        Level(recorder, "events-miss", "ResourceMissed").ShouldBe(EventLevel.Error);
        Level(recorder, "events-miss", "ResourceSkipped").ShouldBe(EventLevel.Warning);
    }

    [Fact(DisplayName = DisplayPrefix + "A run reports a workload that stopped, with its failure")]
    public async Task RunAsync_WhenAWorkloadStops_ShouldReportItsFailure()
    {
        // Arrange
        using var recorder = new EventSourceRecorder(ApplicationGatewayEventSource.Log, EventLevel.Informational);
        var infrastructure = new TestInfrastructure();
        infrastructure.Crashing["db"] = new InvalidOperationException("Disk is full.");
        ApplicationGateway gateway = RunningGateway.Create(infrastructure, new InMemoryApplicationGatewayStore(), CreateModel("events-stop"));

        // Act
        await Should.ThrowAsync<ApplicationGatewayException>(() => gateway.RunAsync());

        // Assert
        Describe(recorder, "events-stop").ShouldContain("ResourceStopped Database.Sql/db System.InvalidOperationException Disk is full.");
    }

    [Fact(DisplayName = DisplayPrefix + "An operator's write reports the action, the recorded state, and an instance the gateway could not restore")]
    public async Task ExecuteAsync_ShouldReportTheActionTheRecordAndADroppedInstance()
    {
        // Arrange
        using var recorder = new EventSourceRecorder(ApplicationGatewayEventSource.Log, EventLevel.Informational);
        var infrastructure = new TestInfrastructure();
        IApplicationModelBuilder builder = ApplicationModel.CreateBuilder("events-action");
        builder.AddResource("Web", "api");
        await using RunningGateway running = await RunningGateway.StartAsync(infrastructure, new InMemoryApplicationGatewayStore(), builder.Build());
        await Task.Delay(5);
        TestInstance second = await infrastructure.StartInstanceAsync("Web", "api");
        infrastructure.Client.Unreachable[second.Key] = true;

        // Act
        await running.Gateway.ExecuteAsync("Web", "api", TestManifests.LogLevel, ApplicationActionContext.Create(
            ApplicationActionOperation.Write, TestSerializer.Json, """{"Level":"Debug"}"""u8.ToArray()));

        // Assert
        string[] events = Describe(recorder, "events-action");
        events.ShouldContain("ActionExecuted Web/api log-level Write Succeeded");
        events.ShouldContain("StateRecorded Web/api log-level 1");
        events.ShouldContain("InstanceDropped Web/api System.Net.Http.HttpRequestException");
        Level(recorder, "events-action", "InstanceDropped").ShouldBe(EventLevel.Warning);
    }

    private static IApplicationModel CreateModel(string gateway)
    {
        IApplicationModelBuilder builder = ApplicationModel.CreateBuilder(gateway);
        IApplicationResourceDefinition database = builder.AddResource("Database.Sql", "db");
        builder.AddResource("Web", "api").DependsOn(database);
        return builder.Build();
    }

    // One gateway's events, each as its name and the payload a test compares; a deployment's id and
    // an instance's key are left out, because they differ per run.
    private static string[] Describe(EventSourceRecorder recorder, string gateway)
    {
        return [.. recorder.Events
            .Where(written => written.Payload is { Count: > 0 } payload && Equals(payload[0], gateway))
            .Select(written => written.EventName switch
            {
                "ResourceDeploying" => $"{written.EventName} {written.Payload![1]}",
                "RunStopped" => written.EventName!,
                "RunFailed" => $"{written.EventName} {written.Payload![1]}",
                "ResourceMissed" => $"{written.EventName} {written.Payload![1]}",
                "ActionExecuted" => $"{written.EventName} {written.Payload![1]} {written.Payload[2]} {written.Payload[3]} {written.Payload[5]}",
                "StateRecorded" => $"{written.EventName} {written.Payload![1]} {written.Payload[2]} {written.Payload[4]}",
                "InstanceDropped" => $"{written.EventName} {written.Payload![1]} {written.Payload[3]}",
                _ => string.Join(" ", [written.EventName, .. written.Payload!.Skip(1)]),
            })];
    }

    private static EventLevel Level(EventSourceRecorder recorder, string gateway, string eventName)
    {
        return recorder.Events.First(written => written.EventName == eventName && Equals(written.Payload![0], gateway)).Level;
    }
}
