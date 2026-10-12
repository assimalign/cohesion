using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Tests;

[Collection(LocalGatewayConsoleCollection.Name)]
public class ApplicationGatewayFailureObservationTests
{
    [Theory(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Reconcile: delayed terminal events retain their own failure detail")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartAsync_ForwardingManagerDelaysFailedEvent_ReportsTerminalDetail(bool multipleModels)
    {
        // Arrange
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var duration = Stopwatch.StartNew();
        int exercised = 0;
        do
        {
            var state = new ForwardingResourceStateManager();
            using var release = new ManualResetEventSlim();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            state.StateChanged += (_, observation) =>
            {
                if (observation.Current == ResourceLifecycle.Failed)
                {
                    entered.TrySetResult();
                    if (!release.Wait(TimeSpan.FromSeconds(10)))
                    {
                        throw new TimeoutException("The test did not release the Failed event.");
                    }
                }
            };
            var reconciled = new TaskCompletionSource<IResourceControlContext>(TaskCreationOptions.RunContinuationsAsynchronously);
            var controller = new RecordingController([], [], leaveStarting: true)
            {
                AfterReconcile = context =>
                {
                    context.State.SetState(context.Resource.Id, ResourceLifecycle.Starting, "Starting old detail");
                    reconciled.TrySetResult(context);
                },
            };
            var gateway = new TestGateway(state, [controller]);
            IApplicationBuilder builder = Application.CreateBuilder("failure-observation", []).UseGateway(gateway);
            builder.AddResource(new TestResource("host"));
            IApplicationBuilder other = Application.CreateBuilder("other", []).UseGateway(gateway);
            other.AddResource(new TestResource("host"));
            IApplicationModel[] models = multipleModels
                ? [builder.Build().Model, other.Build().Model]
                : [builder.Build().Model];
            Task start = ((IMultiModelApplicationGateway)gateway).StartAsync(models, cancellation.Token);
            IResourceControlContext context = await reconciled.Task.WaitAsync(cancellation.Token);
            Task<ResourceLifecycle> readiness = context.State.WaitForStateAsync(context.Resource.Id,
                new HashSet<ResourceLifecycle> { ResourceLifecycle.Failed }, TimeSpan.FromSeconds(10), cancellation.Token);
            Task failure = Task.Run(() => context.State.SetState(context.Resource.Id, ResourceLifecycle.Failed,
                "COHLOCAL001: expected failure detail"), cancellation.Token);
            try
            {
                // Act: the underlying waiter finishes while an earlier event subscriber is held.
                await entered.Task.WaitAsync(cancellation.Token);
                (await readiness).ShouldBe(ResourceLifecycle.Failed);
                await Task.WhenAny(start, Task.Delay(50, cancellation.Token));
                release.Set();
                await failure.WaitAsync(cancellation.Token);
                InvalidOperationException exception = await Should.ThrowAsync<InvalidOperationException>(() => start);

                // Assert
                exception.Message.ShouldContain("COHLOCAL001: expected failure detail", Case.Sensitive);
                exception.Message.ShouldNotContain("Starting old detail", Case.Sensitive);
                exercised++;
            }
            finally
            {
                release.Set();
                await failure.WaitAsync(cancellation.Token);
            }
        }
        while (duration.Elapsed < TimeSpan.FromMilliseconds(250) || exercised < 5);

        exercised.ShouldBeGreaterThanOrEqualTo(5);
    }
}
