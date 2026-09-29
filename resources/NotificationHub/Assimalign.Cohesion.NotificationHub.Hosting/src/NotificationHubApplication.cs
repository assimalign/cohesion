using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.NotificationHub;
using Assimalign.Cohesion.NotificationHub.Hosting.Internal;

namespace Assimalign.Cohesion.NotificationHub.Hosting;

/// <summary>
/// Hosts a NotificationHub application and its ordered service lifecycle.
/// </summary>
public sealed class NotificationHubApplication : Host<NotificationHubApplicationContext>, INotificationHubApplication
{
    private readonly NotificationHubApplicationContext _context;

    internal NotificationHubApplication(
        NotificationHubApplicationOptions options,
        NotificationHubApplicationContext context)
        : base(options)
    {
        _context = context;
    }

    /// <summary>
    /// Gets the concrete application context.
    /// </summary>
    public override NotificationHubApplicationContext Context => _context;

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

    INotificationHubApplicationContext INotificationHubApplication.Context => _context;

    Task INotificationHubApplication.StartAsync(CancellationToken cancellationToken) =>
        ((IHost)this).StartAsync(cancellationToken);

    Task INotificationHubApplication.StopAsync(CancellationToken cancellationToken) =>
        ((IHost)this).StopAsync(cancellationToken);

    /// <summary>
    /// Creates a builder for a notification hub application.
    /// </summary>
    /// <param name="args">The command-line arguments supplied to the application.</param>
    /// <returns>A builder for the notification hub application.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="args"/> is <see langword="null"/>.</exception>
    public static NotificationHubApplicationBuilder CreateBuilder(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        return CreateBuilder(args, Assembly.GetEntryAssembly() ?? typeof(NotificationHubApplication).Assembly);
    }

    internal static NotificationHubApplicationBuilder CreateBuilder(string[] args, Assembly resourceAssembly)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(resourceAssembly);
        return new NotificationHubApplicationBuilder(args, resourceAssembly);
    }
}
