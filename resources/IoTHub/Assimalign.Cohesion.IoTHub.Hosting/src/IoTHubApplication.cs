using System;
using System.Reflection;

using Assimalign.Cohesion.IoTHub;

namespace Assimalign.Cohesion.IoTHub.Hosting;

/// <summary>
/// Creates IoT hub application builders.
/// </summary>
public static class IoTHubApplication
{
    /// <summary>
    /// Creates a builder for an IoT hub application.
    /// </summary>
    /// <param name="args">The command-line arguments supplied to the application.</param>
    /// <returns>A builder for the IoT hub application.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="args"/> is <see langword="null"/>.</exception>
    public static IIoTHubApplicationBuilder CreateBuilder(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        return CreateBuilder(args, Assembly.GetEntryAssembly() ?? typeof(IoTHubApplication).Assembly);
    }

    internal static IIoTHubApplicationBuilder CreateBuilder(string[] args, Assembly resourceAssembly)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(resourceAssembly);
        return new IoTHubApplicationBuilder(args, resourceAssembly);
    }
}
