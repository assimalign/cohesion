using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using Assimalign.Cohesion.DependencyInjection;
using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Scheduler;
using Assimalign.Cohesion.Scheduler.Hosting.Internal;

namespace Assimalign.Cohesion.Scheduler.Hosting;

/// <summary>
/// Composes a Scheduler application and its hosting services.
/// </summary>
/// <remarks>
/// Every dependency the application runs with is a registration in <see cref="Services"/>:
/// declared jobs (<see cref="IScheduleJob"/>), schedule providers (<see cref="IScheduleProvider"/>),
/// and lifecycle services (<see cref="IHostService"/>). The root
/// <see cref="ISchedulerApplicationBuilder"/> verbs are shims over those registrations, so the Cron
/// and Timer packages compose against the dependency-free root contract. <see cref="Build"/>
/// closes registration, creates the service provider once, validates the schedules, and resolves
/// the lifecycle services once, in registration order.
/// </remarks>
[global::System.Diagnostics.DebuggerDisplay("SchedulerApplicationBuilder")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
public sealed class SchedulerApplicationBuilder : ISchedulerApplicationBuilder
{
    private readonly SchedulerApplicationOptions _options;
    private readonly SchedulerApplicationContext _context;
    private bool _isBuilt;

    internal SchedulerApplicationBuilder(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        Services = new ServiceProviderBuilder(new ServiceProviderOptions
        {
            EnableDynamicCode = false,
            ValidateOnBuild = true,
            ValidateScopes = true,
        });

        _options = new SchedulerApplicationOptions
        {
            Environment = AppEnvironment.GetEnvironmentName(),
        };
        _context = new SchedulerApplicationContext(_options);
    }

    /// <summary>
    /// Gets the application's service registrations.
    /// </summary>
    /// <remarks>
    /// Registration closes when <see cref="Build"/> runs; a later registration throws
    /// <see cref="InvalidOperationException"/>. Every <see cref="IHostService"/> registration joins
    /// the application lifecycle in registration order, ahead of the scheduler's own execution
    /// service. Register factory or instance services: the provider is created without dynamic
    /// code. A factory-created service is owned by the application and disposed with it; an
    /// instance stays owned by its caller.
    /// </remarks>
    public ServiceProviderBuilder Services { get; }

    /// <summary>
    /// Declares a job without scheduling it.
    /// </summary>
    /// <remarks>
    /// Cron and Timer feature packages bind declared jobs to providers separately. An unbound
    /// job remains dormant. The job is registered in <see cref="Services"/> as an
    /// <see cref="IScheduleJob"/>; declaring the same instance again has no effect.
    /// </remarks>
    /// <param name="job">The job declaration to register.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="job"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">A different job with the same identifier is registered.</exception>
    public SchedulerApplicationBuilder AddJob(IScheduleJob job)
    {
        ArgumentNullException.ThrowIfNull(job);

        foreach (IScheduleJob declared in GetRegisteredInstances<IScheduleJob>())
        {
            if (declared.Id.Equals(job.Id))
            {
                return ReferenceEquals(declared, job)
                    ? this
                    : throw new InvalidOperationException(
                        $"A different scheduler job with identifier '{job.Id}' is already declared.");
            }
        }

        Services.AddSingleton<IScheduleJob>(job);
        return this;
    }

    /// <summary>
    /// Adds a schedule provider to the scheduler application.
    /// </summary>
    /// <remarks>
    /// The provider is registered in <see cref="Services"/> as an <see cref="IScheduleProvider"/>.
    /// </remarks>
    /// <param name="provider">The provider whose schedules the host executes.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="provider"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The same provider instance is already registered.</exception>
    public SchedulerApplicationBuilder AddScheduleProvider(IScheduleProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);

        foreach (IScheduleProvider registered in GetRegisteredInstances<IScheduleProvider>())
        {
            if (ReferenceEquals(registered, provider))
            {
                throw new InvalidOperationException(
                    "The same scheduler provider instance cannot be registered more than once.");
            }
        }

        Services.AddSingleton<IScheduleProvider>(provider);
        return this;
    }

    /// <summary>
    /// Adds an existing host service to the scheduler application.
    /// </summary>
    /// <remarks>
    /// The service is registered in <see cref="Services"/> and stays owned by the caller.
    /// </remarks>
    /// <param name="service">The service to add.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="service"/> is <see langword="null"/>.</exception>
    public SchedulerApplicationBuilder AddService(IHostService service)
    {
        ArgumentNullException.ThrowIfNull(service);

        Services.AddSingleton<IHostService>(service);
        return this;
    }

    /// <summary>
    /// Adds a host service factory that is invoked once, when the application is built.
    /// </summary>
    /// <remarks>
    /// The application owns and disposes the service the factory returns.
    /// </remarks>
    /// <param name="factory">The factory to invoke with the scheduler host context.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="factory"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="factory"/> returns <see langword="null"/>.</exception>
    public SchedulerApplicationBuilder AddService(Func<SchedulerApplicationContext, IHostService> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        Services.AddSingleton<IHostService>(_ => factory(_context)
            ?? throw new InvalidOperationException("A scheduler service factory returned null."));
        return this;
    }

    /// <summary>
    /// Builds the scheduler application.
    /// </summary>
    /// <returns>The configured scheduler application.</returns>
    /// <exception cref="InvalidOperationException">
    /// The builder has already built an application, a service factory returns null, or a provider
    /// returns invalid, duplicate, or undeclared schedule bindings.
    /// </exception>
    public SchedulerApplication Build()
    {
        InvalidOperationException.ThrowIf(_isBuilt, "The scheduler application has already been built.");

        // Registered last, so schedules execute only after every other service has started and
        // stop before any of them.
        Services.AddSingleton<IHostService>(_ => new SchedulerExecutionService(_context.Schedules));
        Services.AddSingleton<IHostEnvironment>(_context.Environment);
        Services.AddSingleton<ISchedulerApplicationContext>(_context);

        _isBuilt = true;

        // Registration closes here. The provider copies the registrations, so one added after
        // Build would silently never reach it; the read-only container turns that into an error.
        if (Services.Container is ServiceContainer container)
        {
            container.MakeReadOnly();
        }

        _context.SetServiceProvider(((IServiceProviderBuilder)Services).Build());
        try
        {
            // Jobs, providers, and their schedules resolve and validate before any lifecycle
            // service is created.
            _context.ResolveSchedules();
            _context.ResolveHostedServices();

            return new SchedulerApplication(_options, _context);
        }
        catch (Exception exception)
        {
            // The provider owns every service a factory created before the failure.
            try
            {
                Task.Run(() => _context.DisposeServiceProviderAsync().AsTask()).GetAwaiter().GetResult();
            }
            catch (Exception disposalException)
            {
                throw new AggregateException(exception, disposalException);
            }

            throw;
        }
    }

    ISchedulerApplicationBuilder ISchedulerApplicationBuilder.AddJob(IScheduleJob job) => AddJob(job);

    ISchedulerApplicationBuilder ISchedulerApplicationBuilder.AddScheduleProvider(IScheduleProvider provider) =>
        AddScheduleProvider(provider);

    ISchedulerApplication ISchedulerApplicationBuilder.Build() => Build();

    private IEnumerable<T> GetRegisteredInstances<T>()
        where T : class
    {
        foreach (ServiceDescriptor descriptor in Services.Container)
        {
            if (descriptor.ServiceType == typeof(T) && descriptor.ImplementationInstance is T instance)
            {
                yield return instance;
            }
        }
    }

    private sealed class DebugView
    {
        private readonly SchedulerApplicationBuilder _target;

        public DebugView(SchedulerApplicationBuilder value)
        {
            _target = value;
        }

        public ServiceProviderBuilder Services => _target.Services;
    }
}
