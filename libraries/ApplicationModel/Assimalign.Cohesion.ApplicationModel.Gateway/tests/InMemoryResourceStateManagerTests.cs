using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Tests;

public class InMemoryResourceStateManagerTests
{
    private static readonly IReadOnlySet<ResourceLifecycle> RunningOrFailed =
        new HashSet<ResourceLifecycle> { ResourceLifecycle.Running, ResourceLifecycle.Failed };

    private static ResourceId NewId() => Guid.NewGuid();

    [Fact]
    public async Task WaitForStateAsync_AlreadyTerminal_ReturnsImmediately()
    {
        var manager = new InMemoryResourceStateManager();
        ResourceId id = NewId();
        manager.SetState(id, ResourceLifecycle.Running);

        ResourceLifecycle reached = await manager.WaitForStateAsync(id, RunningOrFailed, TimeSpan.FromSeconds(1));

        reached.ShouldBe(ResourceLifecycle.Running);
    }

    [Fact]
    public async Task WaitForStateAsync_SetAfterSubscribe_Completes()
    {
        var manager = new InMemoryResourceStateManager();
        ResourceId id = NewId();

        Task<ResourceLifecycle> wait = manager.WaitForStateAsync(id, RunningOrFailed, TimeSpan.FromSeconds(2));
        manager.SetState(id, ResourceLifecycle.Running);

        (await wait).ShouldBe(ResourceLifecycle.Running);
    }

    [Fact]
    public async Task WaitForStateAsync_Failure_ReturnsFailed()
    {
        var manager = new InMemoryResourceStateManager();
        ResourceId id = NewId();

        Task<ResourceLifecycle> wait = manager.WaitForStateAsync(id, RunningOrFailed, TimeSpan.FromSeconds(2));
        manager.SetState(id, ResourceLifecycle.Failed);

        (await wait).ShouldBe(ResourceLifecycle.Failed);
    }

    [Fact]
    public async Task WaitForStateAsync_Timeout_ReturnsLastObserved()
    {
        var manager = new InMemoryResourceStateManager();
        ResourceId id = NewId();
        manager.SetState(id, ResourceLifecycle.Starting);

        ResourceLifecycle reached = await manager.WaitForStateAsync(
            id,
            new HashSet<ResourceLifecycle> { ResourceLifecycle.Running },
            TimeSpan.FromMilliseconds(50));

        reached.ShouldBe(ResourceLifecycle.Starting);
    }

    [Fact]
    public void SetState_WithObservedEndpoints_ExposesThem()
    {
        var manager = new InMemoryResourceStateManager();
        ResourceId id = NewId();

        manager.SetState(
            id,
            ResourceLifecycle.Running,
            observedEndpoints: new[] { new ResourceEndpoint("http", "http", 8080, Host: "localhost") });

        ResourceEndpoint endpoint = manager.GetObservedEndpoints(id).ShouldHaveSingleItem();
        endpoint.Port.ShouldBe(8080);
        endpoint.Host.ShouldBe("localhost");
    }

    [Fact]
    public void SetState_OnTransition_RaisesStateChanged()
    {
        var manager = new InMemoryResourceStateManager();
        ResourceId id = NewId();
        ResourceStateChangedEventArgs? captured = null;
        manager.StateChanged += (_, args) => captured = args;

        manager.SetState(id, ResourceLifecycle.Running);

        captured.ShouldNotBeNull();
        captured!.Previous.ShouldBe(ResourceLifecycle.Unknown);
        captured.Current.ShouldBe(ResourceLifecycle.Running);
    }
}
