using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Scheduler;
using Assimalign.Cohesion.Scheduler.Hosting.Internal;

namespace Assimalign.Cohesion.Scheduler.Hosting;

/// <summary>
/// Hosts a Scheduler application and its ordered service lifecycle.
/// </summary>
[global::System.Diagnostics.DebuggerDisplay("SchedulerApplication {Id,nq}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
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

    /// <inheritdoc />
    protected override async ValueTask DisposeAsync(bool disposing)
    {
        await base.DisposeAsync(disposing).ConfigureAwait(false);
        if (disposing)
        {
            // The application owns its service provider: disposing it releases every service a
            // registered factory created, after the host has stopped them. Instance registrations
            // stay with their callers.
            await _context.DisposeServiceProviderAsync().ConfigureAwait(false);
        }
    }

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

        return new SchedulerApplicationBuilder(args);
    }

    private sealed class DebugView
    {
        private readonly SchedulerApplication _target;

        public DebugView(SchedulerApplication value)
        {
            _target = value;
        }

        public SchedulerApplicationContext Context => _target.Context;
        public HostId Id => _target.Id;
    }
}
