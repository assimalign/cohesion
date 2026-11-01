using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel.Gateway.Internal;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Tests;

/// <summary>
/// A gateway's run: <see cref="IApplicationGateway.StartAsync"/> completes with startup,
/// <see cref="IApplicationGateway.StopAsync"/> ends the run, and
/// <see cref="IApplicationGateway.Completion"/> reports how it ended.
/// </summary>
public sealed class ApplicationGatewayLifecycleTests
{
    private const string DisplayPrefix = "Cohesion Test [ApplicationModel] - Gateway lifecycle: ";

    [Fact(DisplayName = DisplayPrefix + "Starting completes with startup, and stopping completes the run")]
    public async Task StopAsync_AfterStart_ShouldCompleteTheRun()
    {
        // Arrange
        (ApplicationGateway gateway, TestInfrastructure infrastructure) = CreateGateway();

        // Act
        await gateway.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));
        bool completedWhileRunning = gateway.Completion.IsCompleted;
        await gateway.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));

        // Assert
        completedWhileRunning.ShouldBeFalse();
        gateway.Completion.IsCompletedSuccessfully.ShouldBeTrue();
        gateway.Resources.ShouldHaveSingleItem().Instances.ShouldHaveSingleItem();
        infrastructure.TornDown.ShouldBeEmpty();
    }

    [Fact(DisplayName = DisplayPrefix + "A gateway starts once")]
    public async Task StartAsync_Twice_ShouldThrow()
    {
        // Arrange
        (ApplicationGateway gateway, _) = CreateGateway();
        await gateway.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));

        // Act & Assert
        Should.Throw<InvalidOperationException>(() => gateway.StartAsync());
        await gateway.StopAsync();
    }

    [Fact(DisplayName = DisplayPrefix + "Stopping a gateway that never started does nothing")]
    public async Task StopAsync_BeforeStart_ShouldDoNothing()
    {
        // Arrange
        (ApplicationGateway gateway, TestInfrastructure infrastructure) = CreateGateway();

        // Act
        await gateway.StopAsync();

        // Assert
        gateway.Completion.IsCompleted.ShouldBeFalse();
        infrastructure.Starts.ShouldBe(0);
    }

    [Fact(DisplayName = DisplayPrefix + "Cancelling startup stops the run without a failure")]
    public async Task StartAsync_Cancelled_ShouldCancelStartupAndCompleteTheRun()
    {
        // Arrange
        (ApplicationGateway gateway, TestInfrastructure infrastructure) = CreateGateway();
        infrastructure.Silent.Add("orders-api");
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        // Act
        Task started = gateway.StartAsync(cancel.Token);
        OperationCanceledException exception = await Should.ThrowAsync<OperationCanceledException>(() => started);
        await gateway.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        // Assert
        started.IsCanceled.ShouldBeTrue();
        exception.CancellationToken.ShouldBe(cancel.Token);
        gateway.Completion.IsCompletedSuccessfully.ShouldBeTrue();
        infrastructure.TornDown.ShouldBeEmpty();
    }

    [Fact(DisplayName = DisplayPrefix + "A run a later run supersedes after startup faults its completion")]
    public async Task Completion_WhenSuperseded_ShouldFaultWithRecordSuperseded()
    {
        // Arrange: two gateways of one model over one record store.
        var store = new InMemoryApplicationGatewayStore();
        var infrastructure = new TestInfrastructure();
        IApplicationModel model = CreateModel();
        ApplicationGateway earlier = RunningGateway.Create(infrastructure, store, model);
        await earlier.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));
        ApplicationGateway later = RunningGateway.Create(infrastructure, store, model);

        // Act: the later run takes the record over; the earlier one finds out when it next saves.
        await later.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));
        infrastructure.Gateway = earlier;
        await Should.ThrowAsync<ApplicationGatewayException>(async () => await earlier.PublishAsync(TestManifests.Instance()));
        ApplicationGatewayException exception = await Should.ThrowAsync<ApplicationGatewayException>(() => earlier.Completion.WaitAsync(TimeSpan.FromSeconds(10)));
        await later.StopAsync();

        // Assert
        exception.ErrorCode.ShouldBe(ApplicationGatewayErrorCode.RecordSuperseded);
    }

    [Fact(DisplayName = DisplayPrefix + "A run's completion waits for a save in flight, and no save follows it")]
    public async Task Completion_WithSaveInFlight_ShouldWaitForItAndCloseTheRecord()
    {
        // Arrange: a publication's save is held while the gateway stops.
        var store = new GatedStore();
        var infrastructure = new TestInfrastructure();
        ApplicationGateway gateway = RunningGateway.Create(infrastructure, store, CreateModel());
        await gateway.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));
        store.Hold();
        Task<ApplicationInstancePublication> publication = gateway.PublishAsync(TestManifests.Instance()).AsTask();
        await store.Held.WaitAsync(TimeSpan.FromSeconds(10));

        // Act
        Task stop = gateway.StopAsync();
        await Task.Delay(100);
        bool completedWhileSaving = gateway.Completion.IsCompleted;
        int saves = store.Saves;
        store.Release();
        await stop.WaitAsync(TimeSpan.FromSeconds(10));
        await publication.WaitAsync(TimeSpan.FromSeconds(10));

        // Assert
        completedWhileSaving.ShouldBeFalse();
        store.Saves.ShouldBe(saves + 1);
        gateway.Completion.IsCompletedSuccessfully.ShouldBeTrue();
    }

    [Fact(DisplayName = DisplayPrefix + "A startup failure StartAsync reported leaves no unobserved completion")]
    public async Task Completion_WhenStartupFails_ShouldBeObservedThroughStartAsync()
    {
        // Arrange
        var failure = new InvalidOperationException("channel");
        bool unobserved = false;
        EventHandler<UnobservedTaskExceptionEventArgs> handler = (_, args) =>
        {
            if (args.Exception.InnerExceptions.Contains(failure))
            {
                unobserved = true;
            }
        };
        TaskScheduler.UnobservedTaskException += handler;

        try
        {
            // Act: the caller awaits only StartAsync, then drops the gateway.
            await FailStartupAsync(failure);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            // Assert
            unobserved.ShouldBeFalse();
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= handler;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task FailStartupAsync(Exception failure)
    {
        (ApplicationGateway gateway, TestInfrastructure infrastructure) = CreateGateway();
        infrastructure.StartFailure = failure;
        await Should.ThrowAsync<InvalidOperationException>(() => gateway.StartAsync());
        await gateway.Completion.ContinueWith(static _ => { }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).WaitAsync(TimeSpan.FromSeconds(10));
        gateway.Completion.IsFaulted.ShouldBeTrue();
    }

    [Fact(DisplayName = DisplayPrefix + "RunAsync starts the gateway, waits for its run, and stops it on cancellation")]
    public async Task RunAsync_Cancelled_ShouldStopTheGateway()
    {
        // Arrange
        (ApplicationGateway gateway, TestInfrastructure infrastructure) = CreateGateway();
        using var stop = new CancellationTokenSource();

        // Act
        Task run = ((IApplicationGateway)gateway).RunAsync(stop.Token);
        await Eventually.WaitUntilAsync(() => infrastructure.Instances.Count == 1);
        await stop.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(10));

        // Assert
        run.IsCompletedSuccessfully.ShouldBeTrue();
        gateway.Completion.IsCompletedSuccessfully.ShouldBeTrue();
        infrastructure.Stops.ShouldBe(1);
    }

    private static IApplicationModel CreateModel()
    {
        IApplicationModelBuilder builder = ApplicationModel.CreateBuilder("root");
        builder.AddResource("Web", "orders-api");
        return builder.Build();
    }

    private static (ApplicationGateway Gateway, TestInfrastructure Infrastructure) CreateGateway()
    {
        var infrastructure = new TestInfrastructure();
        return (RunningGateway.Create(infrastructure, new InMemoryApplicationGatewayStore(), CreateModel()), infrastructure);
    }
}
