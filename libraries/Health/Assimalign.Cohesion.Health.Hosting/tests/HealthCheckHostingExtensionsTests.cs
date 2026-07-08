using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.DependencyInjection;
using Assimalign.Cohesion.Health;
using Assimalign.Cohesion.Hosting;

using Shouldly;

namespace Assimalign.Cohesion.Health.Hosting.Tests;

public class HealthCheckHostingExtensionsTests
{
    [Fact(DisplayName = "Cohesion Test [Health.Hosting] - AddHealthChecks: registers a resolvable health-check service reflecting the added checks")]
    public async Task AddHealthChecks_WhenChecksAdded_ShouldResolveServiceThatRunsThem()
    {
        var services = new ServiceProviderBuilder();
        services.AddHealthChecks()
            .AddCheck("db", () => HealthCheckResult.Degraded());

        IServiceProvider provider = ((IServiceProviderBuilder)services).Build();
        var service = provider.GetRequiredService<IHealthCheckService>();

        HealthReport report = await service.CheckHealthAsync();

        report.Entries.ShouldContainKey("db");
        report.Status.ShouldBe(HealthStatus.Degraded);
    }

    [Fact(DisplayName = "Cohesion Test [Health.Hosting] - AddHealthChecks: registers the periodic publisher as a host service")]
    public void AddHealthChecks_WhenCalled_ShouldRegisterPublisherAsHostService()
    {
        var services = new ServiceProviderBuilder();
        services.AddHealthChecks();

        IServiceProvider provider = ((IServiceProviderBuilder)services).Build();

        provider.GetServices<IHostService>().Count().ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Health.Hosting] - Publisher host service: evaluates and publishes on its interval")]
    public async Task PublisherHostService_WhenStarted_ShouldEvaluateAndPublish()
    {
        var recording = new RecordingPublisher();
        var services = new ServiceProviderBuilder();
        services.AddHealthChecks(options =>
            {
                options.Delay = TimeSpan.Zero;
                options.Period = TimeSpan.FromMilliseconds(50);
            })
            .AddCheck("db", () => HealthCheckResult.Degraded());
        services.AddHealthCheckPublisher(recording);

        IServiceProvider provider = ((IServiceProviderBuilder)services).Build();
        IHostService publisher = provider.GetServices<IHostService>().Single();

        await publisher.StartAsync();
        try
        {
            await recording.WaitForFirstReportAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            await publisher.StopAsync();
        }

        recording.LastReport.ShouldNotBeNull();
        recording.LastReport!.Status.ShouldBe(HealthStatus.Degraded);
        recording.LastReport.Entries.ShouldContainKey("db");
    }

    [Fact(DisplayName = "Cohesion Test [Health.Hosting] - Publisher host service: is a no-op when no publishers are registered")]
    public async Task PublisherHostService_WhenNoPublishers_ShouldStartAndStopCleanly()
    {
        var services = new ServiceProviderBuilder();
        services.AddHealthChecks(options => options.Delay = TimeSpan.Zero)
            .AddCheck("db", () => HealthCheckResult.Healthy());

        IServiceProvider provider = ((IServiceProviderBuilder)services).Build();
        IHostService publisher = provider.GetServices<IHostService>().Single();

        await publisher.StartAsync();
        await publisher.StopAsync();

        // No assertion beyond "did not throw" — with no sink the loop exits immediately.
    }

    [Fact(DisplayName = "Cohesion Test [Health.Hosting] - Publisher: honors the configured predicate (readiness slice)")]
    public async Task PublisherHostService_WhenPredicateConfigured_ShouldPublishOnlyMatchingChecks()
    {
        var recording = new RecordingPublisher();
        var services = new ServiceProviderBuilder();
        services.AddHealthChecks(options =>
            {
                options.Delay = TimeSpan.Zero;
                options.Period = TimeSpan.FromMilliseconds(50);
                options.Predicate = HealthCheckPredicates.Ready;
            })
            .AddCheck("ready-db", () => HealthCheckResult.Healthy(), tags: new[] { HealthTags.Ready })
            .AddCheck("live-self", () => HealthCheckResult.Unhealthy(), tags: new[] { HealthTags.Live });
        services.AddHealthCheckPublisher(recording);

        IServiceProvider provider = ((IServiceProviderBuilder)services).Build();
        IHostService publisher = provider.GetServices<IHostService>().Single();

        await publisher.StartAsync();
        try
        {
            await recording.WaitForFirstReportAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            await publisher.StopAsync();
        }

        recording.LastReport!.Entries.Keys.ShouldBe(new[] { "ready-db" });
        recording.LastReport.Status.ShouldBe(HealthStatus.Healthy);
    }

    [Fact(DisplayName = "Cohesion Test [Health.Hosting] - AddHealthCheckPublisher<T>: resolves and drives the registered publisher type")]
    public async Task AddHealthCheckPublisherGeneric_WhenRegistered_ShouldReceiveReports()
    {
        var services = new ServiceProviderBuilder();
        services.AddHealthChecks(options =>
            {
                options.Delay = TimeSpan.Zero;
                options.Period = TimeSpan.FromMilliseconds(50);
            })
            .AddCheck("db", () => HealthCheckResult.Healthy());
        services.AddHealthCheckPublisher<CountingPublisher>();

        IServiceProvider provider = ((IServiceProviderBuilder)services).Build();
        var resolved = provider.GetServices<IHealthPublisher>().OfType<CountingPublisher>().Single();
        IHostService publisher = provider.GetServices<IHostService>().Single();

        await publisher.StartAsync();
        try
        {
            await resolved.WaitForFirstReportAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            await publisher.StopAsync();
        }

        resolved.PublishCount.ShouldBeGreaterThanOrEqualTo(1);
    }

    private sealed class CountingPublisher : IHealthPublisher
    {
        private readonly TaskCompletionSource _first = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int PublishCount { get; private set; }

        public ValueTask PublishAsync(HealthReport report, CancellationToken cancellationToken = default)
        {
            PublishCount++;
            _first.TrySetResult();
            return ValueTask.CompletedTask;
        }

        public Task WaitForFirstReportAsync(TimeSpan timeout) => _first.Task.WaitAsync(timeout);
    }
}
