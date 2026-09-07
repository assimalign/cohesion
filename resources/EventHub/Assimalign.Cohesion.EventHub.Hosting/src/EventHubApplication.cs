using System;

using Assimalign.Cohesion.EventHub;

namespace Assimalign.Cohesion.EventHub.Hosting;

/// <summary>
/// Creates event hub application builders.
/// </summary>
public static class EventHubApplication
{
    /// <summary>
    /// Creates a builder for an event hub application.
    /// </summary>
    /// <param name="args">The command-line arguments supplied to the application.</param>
    /// <returns>A builder for the event hub application.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="args"/> is <see langword="null"/>.</exception>
    public static IEventHubApplicationBuilder CreateBuilder(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        return new EventHubApplicationBuilder(args);
    }
}
