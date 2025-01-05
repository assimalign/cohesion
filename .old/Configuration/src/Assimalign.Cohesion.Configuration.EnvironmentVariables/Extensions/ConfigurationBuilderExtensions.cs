using System;

namespace  Assimalign.Cohesion.Configuration;

using  Assimalign.Cohesion.Configuration.Providers;

public static partial class ConfigurationBuilderExtensions
{
    #region Environment Variable Provider
    // <summary>
    /// Adds an <see cref="IConfigurationProvider"/> that reads configuration values from environment variables.
    /// </summary>
    /// <param name="configurationBuilder">The <see cref="IConfigurationBuilder"/> to add to.</param>
    /// <returns>The <see cref="IConfigurationBuilder"/>.</returns>
    public static IConfigurationBuilder AddEnvironmentVariables(this IConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Add(new ConfigurationEnvironmentVariablesSource());
        return configurationBuilder;
    }

    /// <summary>
    /// Adds an <see cref="IConfigurationProvider"/> that reads configuration values from environment variables
    /// with a specified prefix.
    /// </summary>
    /// <param name="configurationBuilder">The <see cref="IConfigurationBuilder"/> to add to.</param>
    /// <param name="prefix">The prefix that environment variable names must start with. The prefix will be removed from the environment variable names.</param>
    /// <returns>The <see cref="IConfigurationBuilder"/>.</returns>
    public static IConfigurationBuilder AddEnvironmentVariables(
        this IConfigurationBuilder configurationBuilder,
        string prefix)
    {
        configurationBuilder.Add(new ConfigurationEnvironmentVariablesSource { Prefix = prefix });
        return configurationBuilder;
    }

    /// <summary>
    /// Adds an <see cref="IConfigurationProvider"/> that reads configuration values from environment variables.
    /// </summary>
    /// <param name="builder">The <see cref="IConfigurationBuilder"/> to add to.</param>
    /// <param name="configureSource">Configures the source.</param>
    /// <returns>The <see cref="IConfigurationBuilder"/>.</returns>
    public static IConfigurationBuilder AddEnvironmentVariables(this IConfigurationBuilder builder, Action<ConfigurationEnvironmentVariablesSource> configureSource)
        => builder.Add(configureSource);
    #endregion
}
