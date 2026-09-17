using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Hosting.Health;
using Assimalign.Cohesion.Scheduler;

namespace Assimalign.Cohesion.Scheduler.Hosting;

/// <summary>
/// Provides the Scheduler application environment and runtime composition.
/// </summary>
public sealed class SchedulerApplicationContext : HostContext, ISchedulerApplicationContext, IHealthContributor
{
    private readonly IHostEnvironment _environment;
    private IReadOnlyList<IHostService> _hostedServices = Array.Empty<IHostService>();

    internal SchedulerApplicationContext(
        SchedulerApplicationOptions options,
        IReadOnlyList<IScheduleJob> jobs,
        IReadOnlyList<IScheduleProvider> scheduleProviders,
        IReadOnlyList<ISchedule> schedules)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(scheduleProviders);
        ArgumentNullException.ThrowIfNull(schedules);

        _environment = new HostEnvironment(options.Environment ?? "production")
        {
            ContentRootPath = options.ContentRootPath,
        };
        Jobs = new ReadOnlyCollection<IScheduleJob>(jobs.ToArray());
        ScheduleProviders = new ReadOnlyCollection<IScheduleProvider>(scheduleProviders.ToArray());
        Schedules = new ReadOnlyCollection<ISchedule>(schedules.ToArray());
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
    public override IEnumerable<IHostService> HostedServices => _hostedServices;

    /// <summary>
    /// Gets all declared jobs, including jobs without a schedule.
    /// </summary>
    public IReadOnlyList<IScheduleJob> Jobs { get; }

    /// <summary>
    /// Gets the registered schedule providers.
    /// </summary>
    public IReadOnlyList<IScheduleProvider> ScheduleProviders { get; }

    internal IReadOnlyList<ISchedule> Schedules { get; }

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

    internal void SetHostedServices(IReadOnlyList<IHostService> hostedServices)
    {
        ArgumentNullException.ThrowIfNull(hostedServices);
        _hostedServices = new ReadOnlyCollection<IHostService>(hostedServices.ToArray());
    }
}
