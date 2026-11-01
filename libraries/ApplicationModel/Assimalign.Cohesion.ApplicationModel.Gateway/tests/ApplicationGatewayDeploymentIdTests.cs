using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel.Gateway.Internal;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Tests;

/// <summary>
/// A deployment's id: the gateway derives it from what it deploys, the platform hands it to every
/// instance the deployment starts, and only an instance that repeats it makes the resource ready.
/// </summary>
public sealed class ApplicationGatewayDeploymentIdTests
{
    private const string DisplayPrefix = "Cohesion Test [ApplicationModel] - ApplicationGateway deployment: ";

    [Fact(DisplayName = DisplayPrefix + "An instance of an earlier deployment that publishes during an update does not make the resource ready")]
    public async Task PublishAsync_FromAnEarlierDeployment_ShouldNotMakeTheUpdateReady()
    {
        // Arrange: a first run deploys revision 1; the second updates to revision 2, whose instance
        // starts only when the test lets it.
        var infrastructure = new TestInfrastructure();
        var store = new InMemoryApplicationGatewayStore();
        IApplicationModel model = CreateModel("hello");
        await using (await RunningGateway.StartAsync(infrastructure, store, model))
        {
        }

        DeploymentId earlier = infrastructure.RunningDeployments["orders-api"];
        infrastructure.Revisions["orders-api"] = "2";
        var release = new TaskCompletionSource();
        infrastructure.Delayed["orders-api"] = () => release.Task;
        ApplicationGateway gateway = RunningGateway.Create(infrastructure, store, model);
        using var stop = new CancellationTokenSource();
        Task run = gateway.RunAsync(stop.Token, out Task gatewayStarted);

        // Act: while the update rolls out, an instance of the earlier deployment restarts and
        // publishes, as a pod of the previous rollout does.
        await Eventually.WaitUntilAsync(() => infrastructure.Deployments.Count == 2);
        TestInstance restarted = await infrastructure.StartInstanceAsync("Web", "orders-api", earlier);
        await Task.Delay(TimeSpan.FromMilliseconds(300));
        int outputsBeforeRelease = infrastructure.Events().Count(call => call == "outputs:orders-api");
        bool startedBeforeRelease = gatewayStarted.IsCompleted;
        release.SetResult();
        await gatewayStarted.WaitAsync(TimeSpan.FromSeconds(10));
        IApplicationGatewayDeployment resource = gateway.Resources.ShouldHaveSingleItem();
        ApplicationInstanceManifest[] instances = [.. resource.Instances];
        await stop.CancelAsync();
        await run;

        // Assert: the restarted instance registered, but only the update's own instance made the
        // resource ready.
        startedBeforeRelease.ShouldBeFalse();
        outputsBeforeRelease.ShouldBe(1);
        DeploymentId current = infrastructure.RunningDeployments["orders-api"];
        current.ShouldNotBe(earlier);
        instances.ShouldContain(instance => instance.Key == restarted.Key && instance.Deployment == earlier);
        instances.ShouldContain(instance => instance.Deployment == current);
    }

    [Fact(DisplayName = DisplayPrefix + "An instance that publishes the planned deployment before its resource is planned makes the resource ready")]
    public async Task PublishAsync_BeforeThePlanWithThePlannedDeployment_ShouldMakeTheResourceReady()
    {
        // Arrange: a first run deploys the resource. The second runs with a fresh store, while the
        // platform's rollout of the same workload has not finished, so it plans an update to what
        // runs; deploying it again starts nothing new, as applying an unchanged pod template does not.
        var infrastructure = new TestInfrastructure();
        IApplicationModel model = CreateModel("hello");
        await using (await RunningGateway.StartAsync(infrastructure, new InMemoryApplicationGatewayStore(), model))
        {
        }

        DeploymentId running = infrastructure.RunningDeployments["orders-api"];
        infrastructure.RollingOut.Add("orders-api");
        infrastructure.Silent.Add("orders-api");
        var planned = new TaskCompletionSource();
        infrastructure.PlanDelays["orders-api"] = () => planned.Task;
        ApplicationGateway gateway = RunningGateway.Create(infrastructure, new InMemoryApplicationGatewayStore(), model, TimeSpan.FromSeconds(2));
        using var stop = new CancellationTokenSource();
        Task run = gateway.RunAsync(stop.Token, out Task gatewayStarted);

        // Act: while the resource's step plans it, a restarted instance of the running workload
        // publishes, then the plan completes.
        await Eventually.WaitUntilAsync(() => infrastructure.Events().Count(call => call == "plan:orders-api") == 2);
        TestInstance restarted = await infrastructure.StartInstanceAsync("Web", "orders-api", running);
        planned.SetResult();
        await gatewayStarted.WaitAsync(TimeSpan.FromSeconds(10));
        ApplicationInstanceManifest[] instances = [.. gateway.Resources.ShouldHaveSingleItem().Instances];
        await stop.CancelAsync();
        await run;

        // Assert: the plan's deployment is the one the instance repeats, so it made the resource
        // ready, and startup completed within the window.
        infrastructure.Deployments.Last().Deployment.ShouldBe(running);
        instances.ShouldContain(instance => instance.Key == restarted.Key && instance.Deployment == running);
    }

    [Fact(DisplayName = DisplayPrefix + "An instance of another deployment that publishes before its resource is planned does not make the resource ready")]
    public async Task PublishAsync_BeforeThePlanWithAnotherDeployment_ShouldNotMakeTheResourceReady()
    {
        // Arrange: as above, but the instance that publishes early repeats a deployment the plan
        // does not make.
        var infrastructure = new TestInfrastructure();
        IApplicationModel model = CreateModel("hello");
        await using (await RunningGateway.StartAsync(infrastructure, new InMemoryApplicationGatewayStore(), model))
        {
        }

        infrastructure.RollingOut.Add("orders-api");
        infrastructure.Silent.Add("orders-api");
        var planned = new TaskCompletionSource();
        infrastructure.PlanDelays["orders-api"] = () => planned.Task;
        ApplicationGateway gateway = RunningGateway.Create(infrastructure, new InMemoryApplicationGatewayStore(), model, TimeSpan.FromSeconds(1));
        using var stop = new CancellationTokenSource();
        Task run = gateway.RunAsync(stop.Token, out _);

        // Act
        await Eventually.WaitUntilAsync(() => infrastructure.Events().Count(call => call == "plan:orders-api") == 2);
        TestInstance other = await infrastructure.StartInstanceAsync("Web", "orders-api", DeploymentId.New());
        planned.SetResult();
        ApplicationGatewayException exception = await Should.ThrowAsync<ApplicationGatewayException>(() => run.WaitAsync(TimeSpan.FromSeconds(10)));

        // Assert: the window closed without an instance of the plan's deployment.
        exception.ErrorCode.ShouldBe(ApplicationGatewayErrorCode.StartupTimedOut);
        exception.Message.ShouldContain("Web/orders-api", Case.Sensitive);
        other.Deployment.ShouldNotBe(infrastructure.Deployments.Last().Deployment);
    }

    [Fact(DisplayName = DisplayPrefix + "Deploying the same inputs gives the same deployment, and another setting value another")]
    public async Task DeployResourceAsync_WithSameInputs_ShouldReceiveTheSameDeployment()
    {
        // Arrange: three platforms each run one model; the third's setting differs.
        var first = new TestInfrastructure();
        var second = new TestInfrastructure();
        var third = new TestInfrastructure();

        // Act
        await using (await RunningGateway.StartAsync(first, new InMemoryApplicationGatewayStore(), CreateModel("hello")))
        {
        }

        await using (await RunningGateway.StartAsync(second, new InMemoryApplicationGatewayStore(), CreateModel("hello")))
        {
        }

        await using (await RunningGateway.StartAsync(third, new InMemoryApplicationGatewayStore(), CreateModel("welcome")))
        {
        }

        // Assert
        DeploymentId deployment = first.Deployments.ShouldHaveSingleItem().Deployment;
        second.Deployments.ShouldHaveSingleItem().Deployment.ShouldBe(deployment);
        third.Deployments.ShouldHaveSingleItem().Deployment.ShouldNotBe(deployment);
        first.Instances.Values.ShouldHaveSingleItem().Instance.Deployment.ShouldBe(deployment);
    }

    private static IApplicationModel CreateModel(string greeting)
    {
        IApplicationModelBuilder builder = ApplicationModel.CreateBuilder("root");
        builder.AddResource("Web", "orders-api").WithSetting("Greeting", greeting);
        return builder.Build();
    }
}
