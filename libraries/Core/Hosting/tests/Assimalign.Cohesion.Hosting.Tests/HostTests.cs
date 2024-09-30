using System;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using Xunit;


namespace Assimalign.Cohesion.Hosting.Tests;


public class HostTests
{
    public const string DisplayPrefix = $"Cohesion Test [Hosting] - Host: ";

    [Fact(DisplayName = DisplayPrefix + "Ensure Lifecycle Service Start & Stop Order")]
    public async Task TestLifecycleServiceOrder()
    {
        var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var queue = new Queue<TestLifecycleService.Lifecycle>();
        var builder = HostBuilder.Create();

        builder.AddService(new TestLifecycleService(queue.Enqueue));

        using var host = builder.Build();

        await host.RunAsync(cancellationTokenSource.Token);

        Assert.Equal(6, queue.Count);

        // Ensure lifecycle order
        Assert.Equal(TestLifecycleService.Lifecycle.Starting, queue.Dequeue());
        Assert.Equal(TestLifecycleService.Lifecycle.Start, queue.Dequeue());
        Assert.Equal(TestLifecycleService.Lifecycle.Started, queue.Dequeue());
        Assert.Equal(TestLifecycleService.Lifecycle.Stopping, queue.Dequeue());
        Assert.Equal(TestLifecycleService.Lifecycle.Stop, queue.Dequeue());
        Assert.Equal(TestLifecycleService.Lifecycle.Stopped, queue.Dequeue());
    }
}