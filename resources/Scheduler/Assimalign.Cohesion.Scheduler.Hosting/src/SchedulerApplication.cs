using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Scheduler;

namespace Assimalign.Cohesion.Scheduler.Hosting;

/// <summary>
/// Hosts a Scheduler application and its ordered service lifecycle.
/// </summary>
public sealed class SchedulerApplication : Host<SchedulerApplicationContext>, ISchedulerApplication
{
    private readonly SchedulerApplicationContext _context;

    internal SchedulerApplication(
        SchedulerApplicationOptions options,
        SchedulerApplicationContext context)
        : base(options)
    {
        _context = context;
    }

    /// <summary>
    /// Gets the concrete application context.
    /// </summary>
    public override SchedulerApplicationContext Context => _context;

    ISchedulerApplicationContext ISchedulerApplication.Context => _context;

    Task ISchedulerApplication.StartAsync(CancellationToken cancellationToken) =>
        ((IHost)this).StartAsync(cancellationToken);

    Task ISchedulerApplication.StopAsync(CancellationToken cancellationToken) =>
        ((IHost)this).StopAsync(cancellationToken);

    /// <summary>
    /// Creates a builder for a scheduler application.
    /// </summary>
    /// <param name="args">The command-line arguments supplied to the application.</param>
    /// <returns>A builder for the scheduler application.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="args"/> is <see langword="null"/>.</exception>
    public static SchedulerApplicationBuilder CreateBuilder(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        Assembly resourceAssembly = Assembly.GetEntryAssembly() ?? typeof(SchedulerApplication).Assembly;
        return new SchedulerApplicationBuilder(args, resourceAssembly);
    }

    internal static SchedulerApplicationBuilder CreateBuilder(string[] args, Assembly resourceAssembly)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(resourceAssembly);

        return new SchedulerApplicationBuilder(args, resourceAssembly);
    }
}
