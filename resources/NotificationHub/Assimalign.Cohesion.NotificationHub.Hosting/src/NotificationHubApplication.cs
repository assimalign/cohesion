using System;

using Assimalign.Cohesion.NotificationHub;

namespace Assimalign.Cohesion.NotificationHub.Hosting;

/// <summary>
/// Creates notification hub application builders.
/// </summary>
public static class NotificationHubApplication
{
    /// <summary>
    /// Creates a builder for a notification hub application.
    /// </summary>
    /// <param name="args">The command-line arguments supplied to the application.</param>
    /// <returns>A builder for the notification hub application.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="args"/> is <see langword="null"/>.</exception>
    public static INotificationHubApplicationBuilder CreateBuilder(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        return new NotificationHubApplicationBuilder(args);
    }
}
