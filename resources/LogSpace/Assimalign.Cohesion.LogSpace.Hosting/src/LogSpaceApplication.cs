using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.LogSpace;
using Assimalign.Cohesion.LogSpace.Hosting.Internal;

namespace Assimalign.Cohesion.LogSpace.Hosting;

/// <summary>
/// Hosts a LogSpace application and its ordered service lifecycle.
/// </summary>
[global::System.Diagnostics.DebuggerDisplay("LogSpaceApplication {Id,nq}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
public sealed class LogSpaceApplication : Host<LogSpaceApplicationContext>, ILogSpaceApplication
{
    private readonly LogSpaceApplicationContext _context;

    internal LogSpaceApplication(
        LogSpaceApplicationOptions options,
        LogSpaceApplicationContext context)
        : base(options)
    {
        _context = context;
    }

    /// <summary>
    /// Gets the concrete application context.
    /// </summary>
    public override LogSpaceApplicationContext Context => _context;

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

    ILogSpaceApplicationContext ILogSpaceApplication.Context => _context;

    Task ILogSpaceApplication.StartAsync(CancellationToken cancellationToken) =>
        ((IHost)this).StartAsync(cancellationToken);

    Task ILogSpaceApplication.StopAsync(CancellationToken cancellationToken) =>
        ((IHost)this).StopAsync(cancellationToken);

    /// <summary>
    /// Creates a builder for a LogSpace application.
    /// </summary>
    /// <param name="args">The command-line arguments supplied to the application.</param>
    /// <returns>A builder for the LogSpace application.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="args"/> is <see langword="null"/>.</exception>
    public static LogSpaceApplicationBuilder CreateBuilder(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        return new LogSpaceApplicationBuilder(args);
    }

    private sealed class DebugView
    {
        private readonly LogSpaceApplication _target;

        public DebugView(LogSpaceApplication value)
        {
            _target = value;
        }

        public LogSpaceApplicationContext Context => _target.Context;
        public HostId Id => _target.Id;
    }
}
