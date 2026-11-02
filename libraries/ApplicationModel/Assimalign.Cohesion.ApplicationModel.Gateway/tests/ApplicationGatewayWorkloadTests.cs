using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel.Gateway.Internal;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Tests;

/// <summary>
/// A deployment's workload that stops before any instance publishes: the provider reports it, and
/// the gateway fails the resource's step at once with the reason instead of waiting out its window.
/// </summary>
public sealed class ApplicationGatewayWorkloadTests
{
    private const string DisplayPrefix = "Cohesion Test [ApplicationModel] - ApplicationGateway workload: ";

    private static readonly TimeSpan _longWindow = TimeSpan.FromSeconds(30);

    private readonly TestInfrastructure _infrastructure = new();
    private readonly InMemoryApplicationGatewayStore _store = new();

    [Fact(DisplayName = DisplayPrefix + "A workload that fails before it publishes fails the run at once, with its exception")]
    public async Task RunAsync_WhenAWorkloadFailsBeforeItPublishes_ShouldFailAtOnceWithItsException()
    {
        // Arrange: api depends on db, whose workload fails; cache depends on nothing.
        var failure = new InvalidOperationException("Port 5432 is already in use.");
        _infrastructure.Crashing["db"] = failure;
        ApplicationGateway gateway = RunningGateway.Create(_infrastructure, _store, CreateModel(), _longWindow);
        long started = Stopwatch.GetTimestamp();

        // Act
        ApplicationGatewayException exception = await Should.ThrowAsync<ApplicationGatewayException>(() => gateway.RunAsync());
        TimeSpan elapsed = Stopwatch.GetElapsedTime(started);

        // Assert: the run did not wait out the window, names the failure and what it skipped, and
        // carries the workload's own exception.
        elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10));
        exception.ErrorCode.ShouldBe(ApplicationGatewayErrorCode.ResourceFailed);
        exception.Message.ShouldContain("Database.Sql/db (InvalidOperationException: Port 5432 is already in use.)", Case.Sensitive);
        exception.Message.ShouldContain("did not start: Web/api.", Case.Sensitive);
        exception.InnerException.ShouldBeSameAs(failure);
        _infrastructure.TornDown.Select(name => name.Value).ShouldBe(["db"]);
        _infrastructure.Deployed.ShouldNotContain("root:Web/api");
        _infrastructure.Deployed.ShouldContain("root:Caching/cache");
        ApplicationGatewayRecord record = (await _store.LoadAsync("root")).ShouldNotBeNull();
        record.Resources.Select(resource => resource.Name.Value).ShouldBe(["cache"]);
    }

    [Fact(DisplayName = DisplayPrefix + "A workload that ends before it publishes fails the run at once, with no inner exception")]
    public async Task RunAsync_WhenAWorkloadEndsBeforeItPublishes_ShouldFailAtOnce()
    {
        // Arrange
        _infrastructure.Crashing["db"] = null;
        ApplicationGateway gateway = RunningGateway.Create(_infrastructure, _store, CreateModel(), _longWindow);
        long started = Stopwatch.GetTimestamp();

        // Act
        ApplicationGatewayException exception = await Should.ThrowAsync<ApplicationGatewayException>(() => gateway.RunAsync());

        // Assert
        Stopwatch.GetElapsedTime(started).ShouldBeLessThan(TimeSpan.FromSeconds(10));
        exception.ErrorCode.ShouldBe(ApplicationGatewayErrorCode.ResourceFailed);
        exception.Message.ShouldContain("Database.Sql/db (ended)", Case.Sensitive);
        exception.InnerException.ShouldBeNull();
    }

    [Fact(DisplayName = DisplayPrefix + "A run reports a failed workload and a missed window together")]
    public async Task RunAsync_WhenOneWorkloadFailsAndAnotherMissesItsWindow_ShouldReportBoth()
    {
        // Arrange: db fails at once; cache never publishes and its short window closes.
        var failure = new InvalidOperationException("Disk is full.");
        _infrastructure.Crashing["db"] = failure;
        _infrastructure.Silent.Add("cache");
        ApplicationGateway gateway = RunningGateway.Create(_infrastructure, _store, CreateModel(), TimeSpan.FromMilliseconds(300));

        // Act
        ApplicationGatewayException exception = await Should.ThrowAsync<ApplicationGatewayException>(() => gateway.RunAsync());

        // Assert
        exception.ErrorCode.ShouldBe(ApplicationGatewayErrorCode.ResourceFailed);
        exception.Message.ShouldContain("Database.Sql/db (InvalidOperationException: Disk is full.)", Case.Sensitive);
        exception.Message.ShouldContain("1 more published no manifest within 00:00:00.3000000: Caching/cache.", Case.Sensitive);
        exception.InnerException.ShouldBeSameAs(failure);
    }

    [Fact(DisplayName = DisplayPrefix + "The provider watches a deployment until an instance publishes")]
    public async Task RunAsync_WhenAnInstancePublishes_ShouldStopWatching()
    {
        // Act
        await using RunningGateway running = await RunningGateway.StartAsync(_infrastructure, _store, CreateModel(), _longWindow);

        // Assert: every deployment was watched, and the run started.
        string[] events = _infrastructure.Events();
        foreach (string resource in (string[])["db", "cache", "api"])
        {
            Array.IndexOf(events, $"watch:{resource}").ShouldBeGreaterThan(Array.IndexOf(events, $"deploy:{resource}"));
        }

        running.Started.IsCompletedSuccessfully.ShouldBeTrue();
    }

    private static IApplicationModel CreateModel()
    {
        IApplicationModelBuilder builder = ApplicationModel.CreateBuilder("root");
        IApplicationResourceDefinition database = builder.AddResource("Database.Sql", "db");
        builder.AddResource("Caching", "cache");
        builder.AddResource("Web", "api").DependsOn(database);
        return builder.Build();
    }
}
