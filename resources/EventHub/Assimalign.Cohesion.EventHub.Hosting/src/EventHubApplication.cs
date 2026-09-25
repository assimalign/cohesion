using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.EventHub;
using Assimalign.Cohesion.EventHub.Hosting.Internal;
using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.EventHub.Hosting;

/// <summary>
/// Hosts an EventHub application and its ordered service lifecycle.
/// </summary>
public sealed class EventHubApplication : Host<EventHubApplicationContext>, IEventHubApplication
{
    private readonly EventHubApplicationContext _context;

    internal EventHubApplication(
        EventHubApplicationOptions options,
        EventHubApplicationContext context)
        : base(options)
    {
        _context = context;
    }

    /// <summary>
    /// Gets the concrete application context.
    /// </summary>
    public override EventHubApplicationContext Context => _context;

    IEventHubApplicationContext IEventHubApplication.Context => _context;

    Task IEventHubApplication.StartAsync(CancellationToken cancellationToken) =>
        ((IHost)this).StartAsync(cancellationToken);

    Task IEventHubApplication.StopAsync(CancellationToken cancellationToken) =>
        ((IHost)this).StopAsync(cancellationToken);

    /// <summary>
    /// Creates a builder for an event hub application.
    /// </summary>
    /// <param name="args">The command-line arguments supplied to the application.</param>
    /// <returns>A builder for the event hub application.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="args"/> is <see langword="null"/>.</exception>
    public static EventHubApplicationBuilder CreateBuilder(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        return CreateBuilder(args, Assembly.GetEntryAssembly() ?? typeof(EventHubApplication).Assembly);
    }

    internal static EventHubApplicationBuilder CreateBuilder(string[] args, Assembly resourceAssembly)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(resourceAssembly);
        return new EventHubApplicationBuilder(args, resourceAssembly);
    }
}
