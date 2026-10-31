using System;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel.Gateway.Internal;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Tests;

/// <summary>
/// An operator's action through the gateway: it runs on one instance's control plane, and a write is
/// recorded and restored on every other instance, now or when the instance next publishes.
/// </summary>
public sealed class ApplicationGatewayActionTests
{
    private const string DisplayPrefix = "Cohesion Test [ApplicationModel] - ApplicationGateway actions: ";

    private static readonly TimeSpan _shortLease = TimeSpan.FromMilliseconds(300);

    private readonly TestInfrastructure _infrastructure = new();
    private readonly InMemoryApplicationGatewayStore _store = new();

    [Fact(DisplayName = DisplayPrefix + "A write runs on the earliest instance, is recorded, and is restored on every other instance")]
    public async Task ExecuteAsync_WithStatefulWrite_ShouldRecordItAndRestoreItOnEveryInstance()
    {
        // Arrange
        await using RunningGateway running = await StartAsync();
        TestInstance first = _infrastructure.Instances.Values.Single().Instance;
        TestInstance second = await StartAnotherInstanceAsync();
        TestInstance third = await StartAnotherInstanceAsync();

        // Act
        ApplicationActionResult result = await running.Gateway.ExecuteAsync("Web", "orders-api", TestManifests.LogLevel, Write("Debug"));

        // Assert
        result.Status.ShouldBe(ApplicationActionStatus.Succeeded);
        new[] { first.Level.Level, second.Level.Level, third.Level.Level }.ShouldAllBe(level => level == "Debug");
        _infrastructure.Client.Calls.ShouldBe([$"execute:{first.Key}", $"restore:{second.Key}", $"restore:{third.Key}"], ignoreOrder: true);
        _infrastructure.Client.Calls.First().ShouldBe($"execute:{first.Key}");

        ApplicationGatewayRecord record = (await _store.LoadAsync("root")).ShouldNotBeNull();
        Encoding.UTF8.GetString(record.Resources.ShouldHaveSingleItem().State.ShouldHaveSingleItem().Value.Span).ShouldBe("""{"Level":"Debug"}""");
    }

    [Fact(DisplayName = DisplayPrefix + "Writes run one at a time, so every instance ends with the state the record holds")]
    public async Task ExecuteAsync_WithConcurrentWrites_ShouldLeaveEveryInstanceWithTheRecordedState()
    {
        // Arrange: the first write's restore is slow, so a second write would overtake it.
        await using RunningGateway running = await StartAsync();
        TestInstance first = _infrastructure.Instances.Values.Single().Instance;
        TestInstance second = await StartAnotherInstanceAsync();
        _infrastructure.Client.BeforeRestore = snapshot => Encoding.UTF8.GetString(snapshot.Value.Span).Contains("Debug", StringComparison.Ordinal)
            ? Task.Delay(200)
            : Task.CompletedTask;

        // Act
        Task<ApplicationActionResult> debug = running.Gateway.ExecuteAsync("Web", "orders-api", TestManifests.LogLevel, Write("Debug")).AsTask();
        await Eventually.WaitUntilAsync(() => _infrastructure.Client.Calls.Any(call => call.StartsWith("restore:", StringComparison.Ordinal)));
        await running.Gateway.ExecuteAsync("Web", "orders-api", TestManifests.LogLevel, Write("Warning"));
        await debug;

        // Assert
        Encoding.UTF8.GetString(running.Gateway.Resources.Single().State.ShouldHaveSingleItem().Value.Span).ShouldBe("""{"Level":"Warning"}""");
        first.Level.Level.ShouldBe("Warning");
        second.Level.Level.ShouldBe("Warning");
    }

    [Fact(DisplayName = DisplayPrefix + "An instance that starts after a write restores it from its publication's answer")]
    public async Task ExecuteAsync_ThenAnInstanceStarts_ShouldRestoreTheWriteWhenItPublishes()
    {
        // Arrange
        await using RunningGateway running = await StartAsync();
        await running.Gateway.ExecuteAsync("Web", "orders-api", TestManifests.LogLevel, Write("Debug"));

        // Act: a restarted instance is a new one, with a new key.
        TestInstance restarted = await StartAnotherInstanceAsync();

        // Assert
        restarted.Level.Level.ShouldBe("Debug");
        running.Gateway.Resources.Single().Instances.Count.ShouldBe(2);
    }

    [Fact(DisplayName = DisplayPrefix + "A read runs on one instance and records nothing")]
    public async Task ExecuteAsync_WithRead_ShouldRunOnOneInstanceAndRecordNothing()
    {
        // Arrange
        await using RunningGateway running = await StartAsync();
        await StartAnotherInstanceAsync();

        // Act
        ApplicationActionResult result = await running.Gateway.ExecuteAsync(
            "Web",
            "orders-api",
            TestManifests.LogLevel,
            ApplicationActionContext.Create(ApplicationActionKind.Read, TestSerializer.Json));

        // Assert
        result.Status.ShouldBe(ApplicationActionStatus.Succeeded);
        Encoding.UTF8.GetString(result.Output.Span).ShouldBe("""{"Level":"Information"}""");
        _infrastructure.Client.Calls.ShouldHaveSingleItem().ShouldStartWith("execute:");
        running.Gateway.Resources.Single().State.ShouldBeEmpty();
    }

    [Fact(DisplayName = DisplayPrefix + "A write the instance rejects is returned and not recorded")]
    public async Task ExecuteAsync_WithRejectedWrite_ShouldRecordNothing()
    {
        // Arrange
        await using RunningGateway running = await StartAsync();
        await StartAnotherInstanceAsync();

        // Act
        ApplicationActionResult result = await running.Gateway.ExecuteAsync(
            "Web",
            "orders-api",
            TestManifests.LogLevel,
            ApplicationActionContext.Create(ApplicationActionKind.Write, TestSerializer.Json, "not json"u8.ToArray()));

        // Assert
        result.Status.ShouldBe(ApplicationActionStatus.Rejected);
        running.Gateway.Resources.Single().State.ShouldBeEmpty();
        _infrastructure.Client.Calls.ShouldHaveSingleItem().ShouldStartWith("execute:");
    }

    [Fact(DisplayName = DisplayPrefix + "An action no instance declares answers not found without calling one")]
    public async Task ExecuteAsync_WithUndeclaredAction_ShouldAnswerNotFound()
    {
        // Arrange
        await using RunningGateway running = await StartAsync();

        // Act
        ApplicationActionResult result = await running.Gateway.ExecuteAsync("Web", "orders-api", TestManifests.FlushCache, Write("Debug"));

        // Assert
        result.Status.ShouldBe(ApplicationActionStatus.NotFound);
        _infrastructure.Client.Calls.ShouldBeEmpty();
    }

    [Fact(DisplayName = DisplayPrefix + "An instance that does not answer is unregistered, the action runs on the next, and it restores the write once it publishes again")]
    public async Task ExecuteAsync_WhenTheEarliestInstanceDoesNotAnswer_ShouldRunOnTheNextAndHealIt()
    {
        // Arrange
        await using RunningGateway running = await StartAsync(_shortLease);
        TestInstance first = _infrastructure.Instances.Values.Single().Instance;
        TestInstance second = await StartAnotherInstanceAsync();
        _infrastructure.Client.Unreachable[first.Key] = true;

        // Act
        ApplicationActionResult result = await running.Gateway.ExecuteAsync("Web", "orders-api", TestManifests.LogLevel, Write("Debug"));

        // Assert: the write ran on the second instance, and the first, which still runs, learns it
        // when its next renewal finds it unknown and it publishes again.
        result.Status.ShouldBe(ApplicationActionStatus.Succeeded);
        second.Level.Level.ShouldBe("Debug");
        _infrastructure.Client.Calls.ShouldBe([$"execute:{first.Key}", $"execute:{second.Key}"]);
        IApplicationGatewayDeployment resource = running.Gateway.Resources.Single();
        await Eventually.WaitUntilAsync(() => first.Level.Level == "Debug" && resource.Instances.Any(instance => instance.Key == first.Key));
    }

    [Fact(DisplayName = DisplayPrefix + "An instance that does not restore a write is unregistered until it publishes again and restores it")]
    public async Task ExecuteAsync_WhenARestoreIsRefused_ShouldUnregisterTheInstanceUntilItPublishesAgain()
    {
        // Arrange
        await using RunningGateway running = await StartAsync(_shortLease);
        TestInstance second = await StartAnotherInstanceAsync();
        _infrastructure.Client.Refusing[second.Key] = true;
        IApplicationGatewayDeployment resource = running.Gateway.Resources.Single();

        // Act
        await running.Gateway.ExecuteAsync("Web", "orders-api", TestManifests.LogLevel, Write("Debug"));

        // Assert
        resource.Instances.ShouldNotContain(instance => instance.Key == second.Key);
        await Eventually.WaitUntilAsync(() => second.Level.Level == "Debug" && resource.Instances.Any(instance => instance.Key == second.Key));
    }

    [Fact(DisplayName = DisplayPrefix + "An action no instance answers fails with InstanceUnavailable and the failure")]
    public async Task ExecuteAsync_WhenNoInstanceAnswers_ShouldThrowInstanceUnavailable()
    {
        // Arrange
        await using RunningGateway running = await StartAsync();
        TestInstance only = _infrastructure.Instances.Values.Single().Instance;
        _infrastructure.Client.Unreachable[only.Key] = true;

        // Act
        ApplicationGatewayException exception = await Should.ThrowAsync<ApplicationGatewayException>(
            () => running.Gateway.ExecuteAsync("Web", "orders-api", TestManifests.LogLevel, Write("Debug")).AsTask());

        // Assert
        exception.ErrorCode.ShouldBe(ApplicationGatewayErrorCode.InstanceUnavailable);
        exception.InnerException.ShouldBeOfType<HttpRequestException>();
        running.Gateway.Resources.Single().Instances.ShouldBeEmpty();
        running.Gateway.Resources.Single().State.ShouldBeEmpty();
    }

    [Fact(DisplayName = DisplayPrefix + "A resource the gateway does not run fails with ResourceUnknown")]
    public async Task ExecuteAsync_OnUnknownResource_ShouldThrowResourceUnknown()
    {
        // Arrange
        await using RunningGateway running = await StartAsync();

        // Act
        ApplicationGatewayException exception = await Should.ThrowAsync<ApplicationGatewayException>(
            () => running.Gateway.ExecuteAsync("Web", "billing-api", TestManifests.LogLevel, Write("Debug")).AsTask());

        // Assert
        exception.ErrorCode.ShouldBe(ApplicationGatewayErrorCode.ResourceUnknown);
    }

    [Fact(DisplayName = DisplayPrefix + "A gateway that is not running fails with PublicationsClosed")]
    public async Task ExecuteAsync_BeforeRun_ShouldThrowPublicationsClosed()
    {
        // Arrange
        ApplicationGateway gateway = RunningGateway.Create(_infrastructure, _store, CreateModel());

        // Act
        ApplicationGatewayException exception = await Should.ThrowAsync<ApplicationGatewayException>(
            () => gateway.ExecuteAsync("Web", "orders-api", TestManifests.LogLevel, Write("Debug")).AsTask());

        // Assert
        exception.ErrorCode.ShouldBe(ApplicationGatewayErrorCode.PublicationsClosed);
    }

    private Task<RunningGateway> StartAsync(TimeSpan? leaseDuration = null)
    {
        return RunningGateway.StartAsync(_infrastructure, _store, CreateModel(), leaseDuration: leaseDuration);
    }

    // Instance keys order instances by start time to the millisecond, so another instance starts a
    // moment after the ones before it.
    private async Task<TestInstance> StartAnotherInstanceAsync()
    {
        await Task.Delay(5);
        return await _infrastructure.StartInstanceAsync("Web", "orders-api");
    }

    private static ApplicationActionContext Write(string level)
    {
        return ApplicationActionContext.Create(ApplicationActionKind.Write, TestSerializer.Json, Encoding.UTF8.GetBytes($$"""{"Level":"{{level}}"}"""));
    }

    private static IApplicationModel CreateModel()
    {
        IApplicationModelBuilder builder = ApplicationModel.CreateBuilder("root");
        builder.AddResource("Web", "orders-api");
        return builder.Build();
    }
}
