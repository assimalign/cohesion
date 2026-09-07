using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Tests;

public class ApplicationGatewayTests
{
    [Fact]
    public async Task StartAsync_ProvisionsInDependencyOrder()
    {
        var reconciled = new List<string>();
        var deleted = new List<string>();
        var state = new InMemoryResourceStateManager();
        var gateway = new TestGateway(state, new[] { new RecordingController(reconciled, deleted) });

        IApplicationModel model = BuildChain(gateway);

        await ((IApplicationGateway)gateway).StartAsync(model);

        reconciled.ShouldBe(new[] { "a", "b", "c" });
    }

    [Fact]
    public async Task StopAsync_TearsDownInReverseOrder()
    {
        var reconciled = new List<string>();
        var deleted = new List<string>();
        var state = new InMemoryResourceStateManager();
        var gateway = new TestGateway(state, new[] { new RecordingController(reconciled, deleted) });

        IApplicationModel model = BuildChain(gateway);
        IApplicationGateway control = gateway;

        await control.StartAsync(model);
        await control.StopAsync();

        deleted.ShouldBe(new[] { "c", "b", "a" });
    }

    [Fact]
    public async Task StartAsync_WhenDependencyFails_BlocksDependentsAndThrows()
    {
        var reconciled = new List<string>();
        var deleted = new List<string>();
        var state = new InMemoryResourceStateManager();
        var failing = new HashSet<string> { "b" };
        var gateway = new TestGateway(state, new[] { new RecordingController(reconciled, deleted, failing) });

        IApplicationBuilder builder = Application.CreateBuilder().UseGateway(gateway);
        IApplicationResourceDescriptor a = builder.AddResource(new TestResource("a"));
        IApplicationResourceDescriptor b = builder.AddResource(new TestResource("b"));
        b.DependsOn(a);
        IApplicationResourceDescriptor c = builder.AddResource(new TestResource("c"));
        c.DependsOn(b);
        IApplicationModel model = builder.Build().Model;

        await Should.ThrowAsync<InvalidOperationException>(
            async () => await ((IApplicationGateway)gateway).StartAsync(model));

        reconciled.ShouldBe(new[] { "a", "b" });   // c is never reconciled
        state.GetState(c.Resource.Id).ShouldBe(ResourceLifecycle.Blocked);
    }

    [Fact]
    public async Task StartAsync_WhenResourceNeverBecomesReady_TimesOutAndThrows()
    {
        var reconciled = new List<string>();
        var deleted = new List<string>();
        var state = new InMemoryResourceStateManager();
        var controller = new RecordingController(reconciled, deleted, leaveStarting: true);
        var gateway = new TestGateway(state, new[] { controller }, readinessBudget: TimeSpan.FromMilliseconds(75));

        IApplicationBuilder builder = Application.CreateBuilder().UseGateway(gateway);
        builder.AddResource(new TestResource("a"));
        IApplicationModel model = builder.Build().Model;

        await Should.ThrowAsync<InvalidOperationException>(
            async () => await ((IApplicationGateway)gateway).StartAsync(model));
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - StartAsync: Should fail when a resource stops before Running")]
    public async Task StartAsync_WhenResourceStopsBeforeRunning_ShouldFailAndBlockDependents()
    {
        // Arrange
        var reconciled = new List<string>();
        var deleted = new List<string>();
        var state = new InMemoryResourceStateManager();
        var observedStates = new Dictionary<string, ResourceLifecycle>
        {
            ["a"] = ResourceLifecycle.Stopped,
        };
        var controller = new RecordingController(reconciled, deleted, observedStates: observedStates);
        var gateway = new TestGateway(state, new[] { controller });
        IApplicationBuilder builder = Application.CreateBuilder().UseGateway(gateway);
        IApplicationResourceDescriptor a = builder.AddResource(new TestResource("a"));
        IApplicationResourceDescriptor b = builder.AddResource(new TestResource("b"));
        b.DependsOn(a);
        IApplicationModel model = builder.Build().Model;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        // Act
        InvalidOperationException exception = await Should.ThrowAsync<InvalidOperationException>(
            async () => await ((IApplicationGateway)gateway).StartAsync(model, cancellation.Token));

        // Assert
        exception.Message.ShouldContain("'Stopped'");
        cancellation.IsCancellationRequested.ShouldBeFalse();
        reconciled.ShouldBe(new[] { "a" });
        state.GetState(b.Resource.Id).ShouldBe(ResourceLifecycle.Blocked);
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - StartAsync: Should not re-gate a dependent after initial readiness degrades")]
    public async Task StartAsync_WhenDependencyDegradesAfterRunning_ShouldNotRegateDependent()
    {
        // Arrange
        var reconciled = new List<string>();
        var deleted = new List<string>();
        var state = new InMemoryResourceStateManager();
        var observedStates = new Dictionary<string, ResourceLifecycle>
        {
            ["a"] = ResourceLifecycle.Degraded,
        };
        var controller = new RecordingController(reconciled, deleted, observedStates: observedStates);
        var gateway = new TestGateway(state, new[] { controller });
        IApplicationBuilder builder = Application.CreateBuilder().UseGateway(gateway);
        IApplicationResourceDescriptor a = builder.AddResource(new TestResource("a"));
        IApplicationResourceDescriptor b = builder.AddResource(new TestResource("b"));
        b.DependsOn(a);
        IApplicationModel model = builder.Build().Model;
        ResourceStateChangedEventArgs? degradation = null;
        state.StateChanged += (_, args) =>
        {
            if (args.Resource == a.Resource.Id && args.Current == ResourceLifecycle.Degraded)
            {
                degradation = args;
            }
        };
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        // Act
        Task start = ((IApplicationGateway)gateway).StartAsync(model, cancellation.Token);
        ResourceLifecycle initial = await state.WaitForStateAsync(
            a.Resource.Id,
            new HashSet<ResourceLifecycle> { ResourceLifecycle.Degraded },
            TimeSpan.FromSeconds(1),
            cancellation.Token);

        // Assert
        initial.ShouldBe(ResourceLifecycle.Degraded);
        reconciled.ShouldBe(new[] { "a" });

        state.SetState(a.Resource.Id, ResourceLifecycle.Running);
        await start.WaitAsync(TimeSpan.FromSeconds(1));
        reconciled.ShouldBe(new[] { "a", "b" });

        state.SetState(a.Resource.Id, ResourceLifecycle.Degraded, "Liveness probe failed.");
        degradation.ShouldNotBeNull();
        degradation!.Previous.ShouldBe(ResourceLifecycle.Running);
        state.GetState(a.Resource.Id).ShouldBe(ResourceLifecycle.Degraded);
        state.GetState(b.Resource.Id).ShouldBe(ResourceLifecycle.Running);

        await ((IApplicationGateway)gateway).StopAsync(cancellation.Token);
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - StartAsync: Should propagate readiness cancellation")]
    public async Task StartAsync_WhenReadinessWaitIsCanceled_ShouldPropagateOperationCanceledException()
    {
        // Arrange
        var reconciled = new List<string>();
        var deleted = new List<string>();
        var state = new InMemoryResourceStateManager();
        var controller = new RecordingController(reconciled, deleted, leaveStarting: true);
        var gateway = new TestGateway(state, new[] { controller });
        IApplicationBuilder builder = Application.CreateBuilder().UseGateway(gateway);
        IApplicationResourceDescriptor resource = builder.AddResource(new TestResource("a"));
        IApplicationModel model = builder.Build().Model;
        using var cancellation = new CancellationTokenSource();
        Task start = ((IApplicationGateway)gateway).StartAsync(model, cancellation.Token);
        ResourceLifecycle reached = await state.WaitForStateAsync(
            resource.Resource.Id,
            new HashSet<ResourceLifecycle> { ResourceLifecycle.Starting },
            TimeSpan.FromSeconds(1));
        reached.ShouldBe(ResourceLifecycle.Starting);

        // Act
        cancellation.Cancel();

        // Assert
        OperationCanceledException exception = await Should.ThrowAsync<OperationCanceledException>(
            async () => await start.WaitAsync(TimeSpan.FromSeconds(2)));
        exception.CancellationToken.ShouldBe(cancellation.Token);
    }

    private static IApplicationModel BuildChain(IApplicationGateway gateway)
    {
        IApplicationBuilder builder = Application.CreateBuilder().UseGateway(gateway);
        IApplicationResourceDescriptor a = builder.AddResource(new TestResource("a"));
        IApplicationResourceDescriptor b = builder.AddResource(new TestResource("b"));
        b.DependsOn(a);
        IApplicationResourceDescriptor c = builder.AddResource(new TestResource("c"));
        c.DependsOn(b);
        return builder.Build().Model;
    }
}
