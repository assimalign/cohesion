using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Hosting.Internal;

namespace Assimalign.Cohesion.Database.Hosting.Tests;

[Collection(DatabaseProvisioningHealthCollection.Name)]
public sealed class DatabaseHealthDiscoveryTests
{
    [Theory(DisplayName = "Cohesion Test [Database.Hosting] - Discovery and health read pending reopens without waiting for the service metadata monitor (#1790)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetHealthAsync_WithReopenMonitorHeld_ShouldReadPublishedSnapshot(bool discovery)
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var engine = new RecordingEngine();
        var builder = DatabaseApplication.CreateBuilder();
        builder.AddEngine(engine);
        await using var application = builder.Build(guard.Token);
        var service = application.Context.ReopenService.ShouldNotBeNull();
        var holder = Task.Factory.StartNew(() =>
        {
            lock (ReopenMonitor(service)) { entered.SetResult(); release.Wait(); }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        await entered.Task.WaitAsync(guard.Token);

        // Act
        var read = Task.Run(async () => discovery ? application.Context.HealthComponentNames.Count
            : (await application.Context.GetHealthAsync(guard.Token)).Count, guard.Token);
        try { await read.WaitAsync(guard.Token); }
        finally
        {
            release.Set();
            await holder;
            try { await read; } catch (OperationCanceledException) when (guard.IsCancellationRequested) { }
        }

        // Assert
        (await read).ShouldBe(2);
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_sync")]
    private static extern ref object ReopenMonitor(DatabaseReopenService service);
}
