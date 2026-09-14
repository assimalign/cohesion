using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Hosting.Resources;
using Assimalign.Cohesion.Hosting.Telemetry;
using Assimalign.Cohesion.Logging;
using Assimalign.Cohesion.Scheduler;

namespace Assimalign.Cohesion.Scheduler.Hosting;

internal sealed class SchedulerApplicationBuilder : ISchedulerApplicationBuilder
{
    private readonly Dictionary<JobId, IScheduleJob> _jobs = [];
    private readonly List<IScheduleProvider> _providers = [];
    private readonly List<Func<IHostContext, IHostService>> _serviceFactories = [];
    private readonly ILoggerFactory? _loggerFactory;
    private readonly IResourceControlPlane? _controlPlane;
    private readonly ResourceContext? _resourceContext;
    private bool _isBuilt;

    internal SchedulerApplicationBuilder(string[] args, Assembly? resourceAssembly = null)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (resourceAssembly is not null &&
            ResourceRuntime.TryCreateControlPlane(resourceAssembly, out IResourceControlPlane? controlPlane))
        {
            _controlPlane = controlPlane ?? throw new InvalidOperationException(
                "The registered Scheduler resource control-plane factory returned null.");
            _resourceContext = ResourceRuntime.Current;
            _loggerFactory = ResourceTelemetry.Configure(_resourceContext, out IHostService? telemetry);
            if (telemetry is not null)
            {
                _serviceFactories.Insert(0, _ => telemetry);
            }
        }
    }

    public ISchedulerApplicationBuilder AddJob(IScheduleJob job)
    {
        ArgumentNullException.ThrowIfNull(job);

        if (_jobs.TryGetValue(job.Id, out IScheduleJob? existing))
        {
            if (!ReferenceEquals(existing, job))
            {
                throw new InvalidOperationException(
                    $"A different scheduler job with identifier '{job.Id}' is already declared.");
            }

            return this;
        }

        _jobs.Add(job.Id, job);
        return this;
    }

    public ISchedulerApplicationBuilder AddScheduleProvider(IScheduleProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);

        for (int index = 0; index < _providers.Count; index++)
        {
            if (ReferenceEquals(_providers[index], provider))
            {
                throw new InvalidOperationException(
                    "The same scheduler provider instance cannot be registered more than once.");
            }
        }

        _providers.Add(provider);
        return this;
    }

    public ISchedulerApplicationBuilder AddService(IHostService service)
    {
        ArgumentNullException.ThrowIfNull(service);

        _serviceFactories.Add(_ => service);
        return this;
    }

    public ISchedulerApplicationBuilder AddService(Func<IHostContext, IHostService> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        _serviceFactories.Add(factory);
        return this;
    }

    public ISchedulerApplication Build()
    {
        if (_isBuilt)
        {
            throw new InvalidOperationException("The scheduler application has already been built.");
        }

        ISchedule[] schedules = GetValidatedSchedules();

        var options = new SchedulerApplicationOptions
        {
            Environment = _resourceContext?.EnvironmentName ?? AppEnvironment.GetEnvironmentName(),
            ContentRootPath = _resourceContext is null
                ? (FileSystemPath?)null
                : FileSystemPath.Parse(_resourceContext.ContentRootPath),
        };
        var context = new SchedulerApplicationContext(
            options,
            [.. _jobs.Values],
            [.. _providers],
            schedules);
        var hostedServices = new List<IHostService>(_serviceFactories.Count + 2);

        for (int index = 0; index < _serviceFactories.Count; index++)
        {
            hostedServices.Add(_serviceFactories[index](context)
                ?? throw new InvalidOperationException("A scheduler service factory returned null."));
        }

        if (_controlPlane is not null && _resourceContext is not null)
        {
            _controlPlane.AddHealthContributor(context);
            if (_resourceContext.Endpoints.TryGetValue("http", out Uri? endpoint))
            {
                _controlPlane.ObserveEndpoint("http", endpoint);
                hostedServices.Add(new SchedulerControlPlaneEndpointService(
                    endpoint,
                    _controlPlane,
                    _resourceContext,
                    context));
            }
        }

        hostedServices.Add(new SchedulerExecutionService(schedules));
        context.SetHostedServices(hostedServices);

        var application = new SchedulerApplicationHost(options, context);
        if (_controlPlane is not null)
        {
            ResourceRuntime.HostBuilt(application, _controlPlane);
        }

        _isBuilt = true;
        return application;
    }

    IHost IHostBuilder.Build() => Build();

    private ISchedule[] GetValidatedSchedules()
    {
        var schedules = new List<ISchedule>();
        var seenSchedules = new HashSet<ISchedule>(ReferenceEqualityComparer.Instance);

        for (int providerIndex = 0; providerIndex < _providers.Count; providerIndex++)
        {
            IEnumerable<ISchedule> providerSchedules = _providers[providerIndex].GetSchedules()
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
                    if (!_jobs.TryGetValue(job.Id, out IScheduleJob? declared) ||
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
