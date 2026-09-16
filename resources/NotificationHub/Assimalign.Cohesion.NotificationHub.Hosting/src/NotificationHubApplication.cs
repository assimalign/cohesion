using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.NotificationHub;

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
