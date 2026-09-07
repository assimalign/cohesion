using System;

using Assimalign.Cohesion.ConfigurationStore;

namespace Assimalign.Cohesion.ConfigurationStore.Hosting;

/// <summary>
/// Creates configuration store application builders.
/// </summary>
public static class ConfigurationStoreApplication
{
    /// <summary>
    /// Creates a builder for a configuration store application.
    /// </summary>
    /// <param name="args">The command-line arguments supplied to the application.</param>
    /// <returns>A builder for the configuration store application.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="args"/> is <see langword="null"/>.</exception>
    public static IConfigurationStoreApplicationBuilder CreateBuilder(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        return new ConfigurationStoreApplicationBuilder(args);
    }
}
