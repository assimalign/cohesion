using System;

using Assimalign.Cohesion.MediaHub;

namespace Assimalign.Cohesion.MediaHub.Hosting;

/// <summary>
/// Creates media hub application builders.
/// </summary>
public static class MediaHubApplication
{
    /// <summary>
    /// Creates a builder for a media hub application.
    /// </summary>
    /// <param name="args">The command-line arguments supplied to the application.</param>
    /// <returns>A builder for the media hub application.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="args"/> is <see langword="null"/>.</exception>
    public static IMediaHubApplicationBuilder CreateBuilder(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        return new MediaHubApplicationBuilder(args);
    }
}
