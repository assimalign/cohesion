using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Tests;

[Collection(LocalGatewayConsoleCollection.Name)]
public class ApplicationGatewayReadinessSourceTests
{
    [Theory(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Readiness: late or missing foreign events cannot decide the gate")]
    [InlineData(ResourceLifecycle.Running, false, false)]
    [InlineData(ResourceLifecycle.Running, true, false)]
    [InlineData(ResourceLifecycle.Running, false, true)]
    [InlineData(ResourceLifecycle.Running, true, true)]
    [InlineData(ResourceLifecycle.Failed, false, false)]
    [InlineData(ResourceLifecycle.Failed, true, false)]
    [InlineData(ResourceLifecycle.Failed, false, true)]
    [InlineData(ResourceLifecycle.Failed, true, true)]
    public async Task StartAsync_ForeignTerminalEventLateOrMissing_UsesManagerLifecycle(
        ResourceLifecycle terminal, bool multipleModels, bool deliverLate)
    {
        // Arrange
        var state = new WithheldEventResourceStateManager { Withhold = observation => observation.Current == terminal };
        var controller = new RecordingController([], [], leaveStarting: true)
        {
            AfterReconcile = context =>
            {
                if (context.Model.Name == (ApplicationName)"other")
                {
                    context.State.SetState(context.Resource.Id, ResourceLifecycle.Skipped);
                    return;
                }

                context.State.SetState(context.Resource.Id, ResourceLifecycle.Starting, "Starting old detail");
                context.State.SetState(context.Resource.Id, terminal, "Undelivered terminal detail");
            },
        };
        var gateway = new TestGateway(state, [controller], readinessBudget: TimeSpan.FromSeconds(10));
        IApplicationModel[] models = CreateModels(gateway, multipleModels);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        IApplicationGateway control = gateway;
        try
        {
            // Act
            Task start = ((IMultiModelApplicationGateway)gateway).StartAsync(models, cancellation.Token);
            if (terminal == ResourceLifecycle.Running)
            {
                await start;
            }
            else
            {
                InvalidOperationException exception = await Should.ThrowAsync<InvalidOperationException>(() => start);
                exception.Message.ShouldContain("observed 'Failed'", Case.Sensitive);
                exception.Message.ShouldContain("last state detail: (none reported).", Case.Sensitive);
                exception.Message.ShouldNotContain("Starting old detail", Case.Sensitive);
                exception.Message.ShouldNotContain("exceeded its readiness budget", Case.Sensitive);
            }

            if (deliverLate)
            {
                state.DeliverWithheldEvents();
            }

            // Assert: the gateway finishes before any terminal event, using the real waiter.
            state.WaitCalls.ShouldBe(1);
        }
        finally
        {
            await control.StopAsync(CancellationToken.None);
        }
    }

    [Theory(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Readiness: reversed built-in events preserve manager state and detail")]
    [InlineData(ResourceLifecycle.Running, false)]
    [InlineData(ResourceLifecycle.Running, true)]
    [InlineData(ResourceLifecycle.Failed, false)]
    [InlineData(ResourceLifecycle.Failed, true)]
    public async Task StartAsync_BuiltInEventsReversed_UsesManagerObservation(ResourceLifecycle terminal, bool multipleModels)
    {
        // Arrange
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var duration = Stopwatch.StartNew();
        int exercised = 0;
        do
        {
            var state = new InMemoryResourceStateManager();
            ResourceLifecycle first = terminal == ResourceLifecycle.Running ? ResourceLifecycle.Failed : ResourceLifecycle.Running;
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var delivered = new List<ResourceLifecycle>();
            state.StateChanged += (_, observation) =>
            {
                if (observation.Current == first)
                {
                    entered.Set();
                    release.Wait(cancellation.Token);
                }
            };
            state.StateChanged += (_, observation) =>
            {
                if (observation.Current == first || observation.Current == terminal)
                {
                    delivered.Add(observation.Current);
                }
            };
            var controller = new RecordingController([], [], leaveStarting: true)
            {
                AfterReconcile = context =>
                {
                    if (context.Model.Name == (ApplicationName)"other")
                    {
                        context.State.SetState(context.Resource.Id, ResourceLifecycle.Skipped);
                        return;
                    }

                    Task earlier = Task.Run(() => context.State.SetState(context.Resource.Id, first, "Earlier detail"), cancellation.Token);
                    try
                    {
                        entered.Wait(cancellation.Token);
                        context.State.SetState(context.Resource.Id, terminal, "Manager terminal detail");
                    }
                    finally
                    {
                        release.Set();
                        earlier.GetAwaiter().GetResult();
                    }
                },
            };
            var gateway = new TestGateway(state, [controller], readinessBudget: TimeSpan.FromSeconds(1));
            IApplicationModel[] models = CreateModels(gateway, multipleModels);
            try
            {
                // Act
                Task start = ((IMultiModelApplicationGateway)gateway).StartAsync(models, cancellation.Token);
                if (terminal == ResourceLifecycle.Running)
                {
                    await start;
                }
                else
                {
                    InvalidOperationException exception = await Should.ThrowAsync<InvalidOperationException>(() => start);
                    exception.Message.ShouldContain("observed 'Failed'", Case.Sensitive);
                    exception.Message.ShouldContain("Manager terminal detail", Case.Sensitive);
                    exception.Message.ShouldNotContain("Earlier detail", Case.Sensitive);
                }

                // Assert
                delivered.ShouldBe([terminal, first]);
                exercised++;
            }
            finally
            {
                await ((IApplicationGateway)gateway).StopAsync(CancellationToken.None);
            }
        }
        while (duration.Elapsed < TimeSpan.FromMilliseconds(250) || exercised < 5);

        exercised.ShouldBeGreaterThanOrEqualTo(5);
    }

    [Theory(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Failure diagnostics: other failed resources come from the foreign manager")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartAsync_OtherForeignStatesDisagreeWithEvents_ReportsOnlyManagerFailures(bool multipleModels)
    {
        // Arrange
        var state = new WithheldEventResourceStateManager();
        IApplicationResource failed = new TestResource("failed");
        IApplicationResource recovered = new TestResource("recovered");
        var controller = new RecordingController([], [], leaveStarting: true)
        {
            AfterReconcile = context =>
            {
                context.State.SetState(failed.Id, ResourceLifecycle.Starting, "Stale starting detail");
                context.State.SetState(recovered.Id, ResourceLifecycle.Failed, "Stale failed detail");
                state.Withhold = observation => observation.Detail == "Withheld detail";
                context.State.SetState(failed.Id, ResourceLifecycle.Failed, "Withheld detail");
                context.State.SetState(recovered.Id, ResourceLifecycle.Running, "Withheld detail");
                context.State.SetState(context.Resource.Id, ResourceLifecycle.Failed, "Gated failure");
            },
        };
        var gateway = new TestGateway(state, [controller]);
        IApplicationModel[] models = CreateModels(gateway, multipleModels, failed, recovered);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // Act
        InvalidOperationException exception = await Should.ThrowAsync<InvalidOperationException>(
            () => ((IMultiModelApplicationGateway)gateway).StartAsync(models, cancellation.Token));

        // Assert
        exception.Message.ShouldContain("Resource 'readiness/failed' last state detail: (none reported).", Case.Sensitive);
        exception.Message.ShouldNotContain("readiness/recovered", Case.Sensitive);
        exception.Message.ShouldNotContain("Stale", Case.Sensitive);
        exception.Message.ShouldContain("Gated failure", Case.Sensitive);
    }

    [Theory(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Failure diagnostics: other built-in failures retain atomic detail before event delivery")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartAsync_OtherBuiltInFailureEventHeld_ReportsAtomicDetail(bool multipleModels)
    {
        // Arrange
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var duration = Stopwatch.StartNew();
        int exercised = 0;
        int delivered = 0;
        do
        {
            var state = new InMemoryResourceStateManager();
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            state.StateChanged += (_, observation) =>
            {
                if (observation.Detail == "Other atomic failure")
                {
                    entered.Set();
                    release.Wait(cancellation.Token);
                    Interlocked.Increment(ref delivered);
                }
            };
            IApplicationResource other = new TestResource("failed");
            Task failure = Task.CompletedTask;
            var controller = new RecordingController([], [], leaveStarting: true)
            {
                AfterReconcile = context =>
                {
                    context.State.SetState(other.Id, ResourceLifecycle.Starting, "Other stale detail");
                    failure = Task.Run(() => context.State.SetState(other.Id, ResourceLifecycle.Failed, "Other atomic failure"), cancellation.Token);
                    entered.Wait(cancellation.Token);
                    context.State.SetState(context.Resource.Id, ResourceLifecycle.Failed, "Gated failure");
                },
            };
            var gateway = new TestGateway(state, [controller]);
            IApplicationModel[] models = CreateModels(gateway, multipleModels, other);
            try
            {
                // Act
                InvalidOperationException exception = await Should.ThrowAsync<InvalidOperationException>(
                    () => ((IMultiModelApplicationGateway)gateway).StartAsync(models, cancellation.Token));

                // Assert
                exception.Message.ShouldContain("Resource 'readiness/failed' last state detail: Other atomic failure", Case.Sensitive);
                exception.Message.ShouldNotContain("Other stale detail", Case.Sensitive);
                exercised++;
            }
            finally
            {
                release.Set();
                await failure.WaitAsync(cancellation.Token);
                await ((IApplicationGateway)gateway).StopAsync(CancellationToken.None);
            }
        }
        while (duration.Elapsed < TimeSpan.FromMilliseconds(250) || exercised < 5);

        exercised.ShouldBeGreaterThanOrEqualTo(5);
        delivered.ShouldBe(exercised);
    }

    private static IApplicationModel[] CreateModels(TestGateway gateway, bool multipleModels, params IApplicationResource[] additional)
    {
        IApplicationBuilder builder = Application.CreateBuilder("readiness", []).UseGateway(gateway);
        builder.AddResource(new TestResource("host"));
        foreach (IApplicationResource resource in additional)
        {
            builder.AddResource(resource);
        }

        IApplicationModel model = builder.Build().Model;
        IApplicationBuilder other = Application.CreateBuilder("other", []).UseGateway(gateway);
        other.AddResource(new TestResource("host"));
        return multipleModels
            ? [model, other.Build().Model]
            : [model];
    }
}
