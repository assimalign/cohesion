using System;
using System.Collections.Generic;
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
