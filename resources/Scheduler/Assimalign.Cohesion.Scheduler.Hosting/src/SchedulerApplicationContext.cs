using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.DependencyInjection;
using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Hosting.Health;
using Assimalign.Cohesion.Scheduler;
using Assimalign.Cohesion.Scheduler.Hosting.Internal;

namespace Assimalign.Cohesion.Scheduler.Hosting;

/// <summary>
/// Provides the Scheduler application environment and runtime composition.
/// </summary>
public sealed class SchedulerApplicationContext : HostContext, ISchedulerApplicationContext, IHealthContributor
{
    private readonly IHostEnvironment _environment;
    private IServiceProvider? _serviceProvider;
    private IReadOnlyList<IHostService> _hostedServices = Array.Empty<IHostService>();
    private int _isServiceProviderDisposed;

    internal SchedulerApplicationContext(SchedulerApplicationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _environment = new HostEnvironment(options.Environment ?? "production")
        {
            ContentRootPath = options.ContentRootPath,
        };
    }

    /// <summary>
    /// Gets the configured application content root.
    /// </summary>
    public FileSystemPath? ContentRootPath => Environment.ContentRootPath;

    /// <summary>
    /// Gets the host environment for this application.
    /// </summary>
    public override IHostEnvironment Environment => _environment;

    /// <summary>
    /// Gets the hosted services in registration and startup order.
    /// </summary>
    /// <remarks>
    /// The <see cref="IHostService"/> registrations, resolved once when the application is built.
    /// </remarks>
    public override IEnumerable<IHostService> HostedServices => _hostedServices;

    /// <summary>
    /// Gets the application's service provider.
    /// </summary>
    /// <exception cref="InvalidOperationException">The application has not been built.</exception>
    public IServiceProvider ServiceProvider => _serviceProvider
        ?? throw new InvalidOperationException("The service provider is created when the scheduler application is built.");

    /// <summary>
    /// Gets all declared jobs, including jobs without a schedule.
    /// </summary>
    /// <remarks>
    /// The <see cref="IScheduleJob"/> registrations, resolved once when the application is built.
    /// </remarks>
    public IReadOnlyList<IScheduleJob> Jobs { get; private set; } = Array.Empty<IScheduleJob>();

    /// <summary>
    /// Gets the registered schedule providers.
    /// </summary>
    /// <remarks>
    /// The <see cref="IScheduleProvider"/> registrations, resolved once when the application is built.
    /// </remarks>
    public IReadOnlyList<IScheduleProvider> ScheduleProviders { get; private set; } = Array.Empty<IScheduleProvider>();

    internal IReadOnlyList<ISchedule> Schedules { get; private set; } = Array.Empty<ISchedule>();

    /// <summary>
    /// Gets the name used for this application health contribution.
    /// </summary>
    public string Name => "scheduler";

    /// <summary>
    /// Reports the health of the application.
    /// </summary>
    /// <param name="cancellationToken">The token that cancels the health check.</param>
    /// <returns>The current application health contribution.</returns>
    /// <exception cref="OperationCanceledException">The cancellation token is cancelled.</exception>
    public ValueTask<HealthContribution> CheckAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string[] failed = Schedules
            .Where(static schedule => schedule.Status is ScheduleStatus.Failed)
            .Select(static schedule => schedule.Name ?? schedule.Id.ToString())
            .ToArray();
        var data = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["jobCount"] = Jobs.Count,
            ["scheduleCount"] = Schedules.Count,
            ["runningCount"] = Schedules.Count(static schedule => schedule.Status is ScheduleStatus.Running),
        };

        HealthContribution contribution = failed.Length == 0
            ? HealthContribution.Healthy($"{Schedules.Count} schedule(s) are available.", data)
            : HealthContribution.Unhealthy(
                $"Failed schedules: {string.Join(", ", failed)}.",
                data);
        return ValueTask.FromResult(contribution);
    }

    internal void SetServiceProvider(IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        _serviceProvider = serviceProvider;
    }

    /// <summary>
    /// Resolves the declared jobs and schedule providers once and validates the schedules the
    /// providers bind.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Two different jobs share an identifier, a provider is registered twice, or a provider returns
    /// invalid, duplicate, or undeclared schedule bindings.
    /// </exception>
    internal void ResolveSchedules()
    {
        var jobs = new Dictionary<JobId, IScheduleJob>();
        var orderedJobs = new List<IScheduleJob>();
        foreach (IScheduleJob job in ServiceProvider.GetRequiredService<IEnumerable<IScheduleJob>>())
        {
            if (jobs.TryGetValue(job.Id, out IScheduleJob? declared))
            {
                if (!ReferenceEquals(declared, job))
                {
                    throw new InvalidOperationException(
                        $"A different scheduler job with identifier '{job.Id}' is already declared.");
                }

                continue;
            }

            jobs.Add(job.Id, job);
            orderedJobs.Add(job);
        }

        var providers = new List<IScheduleProvider>();
        foreach (IScheduleProvider provider in ServiceProvider.GetRequiredService<IEnumerable<IScheduleProvider>>())
        {
            if (providers.Contains(provider, ReferenceEqualityComparer.Instance))
            {
                throw new InvalidOperationException(
                    "The same scheduler provider instance cannot be registered more than once.");
            }

            providers.Add(provider);
        }

        ISchedule[] schedules = GetValidatedSchedules(jobs, providers);
        Jobs = new ReadOnlyCollection<IScheduleJob>(orderedJobs.ToArray());
        ScheduleProviders = new ReadOnlyCollection<IScheduleProvider>(providers.ToArray());
        Schedules = new ReadOnlyCollection<ISchedule>(schedules);
    }

    internal void ResolveHostedServices()
    {
        _hostedServices = new ReadOnlyCollection<IHostService>(
            ServiceProvider.GetRequiredService<IEnumerable<IHostService>>().ToArray());
    }

    /// <summary>
    /// Disposes the service provider, which disposes every service a registered factory created.
    /// </summary>
    internal ValueTask DisposeServiceProviderAsync()
    {
        if (_serviceProvider is IAsyncDisposable serviceProvider &&
            Interlocked.Exchange(ref _isServiceProviderDisposed, 1) == 0)
        {
            return serviceProvider.DisposeAsync();
        }

        return ValueTask.CompletedTask;
    }

    private static ISchedule[] GetValidatedSchedules(
        Dictionary<JobId, IScheduleJob> jobs,
        List<IScheduleProvider> providers)
    {
        var schedules = new List<ISchedule>();
        var seenSchedules = new HashSet<ISchedule>(ReferenceEqualityComparer.Instance);

        for (int providerIndex = 0; providerIndex < providers.Count; providerIndex++)
        {
            IEnumerable<ISchedule> providerSchedules = providers[providerIndex].GetSchedules()
                ?? throw new InvalidOperationException("A scheduler provider returned a null schedule collection.");
            foreach (ISchedule schedule in providerSchedules)
            {
                if (schedule is null)
                {
                    throw new InvalidOperationException("A scheduler provider returned a null schedule.");
                }

                if (!seenSchedules.Add(schedule))
                {
                    throw new InvalidOperationException(
                        $"Schedule '{schedule.Name ?? schedule.Id.ToString()}' was returned more than once. " +
                        "A schedule instance can be executed by only one provider registration.");
                }

                foreach (IScheduleJob job in schedule.Jobs)
                {
                    if (!jobs.TryGetValue(job.Id, out IScheduleJob? declared) ||
                        !ReferenceEquals(declared, job))
                    {
                        throw new InvalidOperationException(
                            $"Schedule '{schedule.Name ?? schedule.Id.ToString()}' binds job " +
                            $"'{job.Name ?? job.Id.ToString()}' before that job was declared with AddJob.");
                    }
                }

                schedules.Add(schedule);
            }
        }

        return [.. schedules];
    }
}
