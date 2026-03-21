using System;

namespace Assimalign.Cohesion.Configuration;

/// <summary>
/// Adds environment variable provider registration helpers to configuration builders.
/// </summary>
public static class ConfigurationBuilderExtensions
{
    /// <summary>
    /// Adds the environment variables provider to the builder.
    /// </summary>
    /// <param name="builder">The builder to add to.</param>
    /// <returns>The current builder.</returns>
    public static IConfigurationBuilder AddEnvironmentVariables(this IConfigurationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.AddProvider(_ => new ConfigurationEnvironmentVariablesProvider());
    }

    /// <summary>
    /// Adds the environment variables provider to the builder with a prefix filter.
    /// </summary>
    /// <param name="builder">The builder to add to.</param>
    /// <param name="prefix">The prefix used to filter environment variable names.</param>
    /// <returns>The current builder.</returns>
    public static IConfigurationBuilder AddEnvironmentVariables(this IConfigurationBuilder builder, string prefix)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.AddProvider(_ => new ConfigurationEnvironmentVariablesProvider(prefix));
    }

    /// <summary>
    /// Adds the environment variables provider to the builder using a configuration callback.
    /// </summary>
    /// <param name="builder">The builder to add to.</param>
    /// <param name="configureSource">The callback used to configure provider options.</param>
    /// <returns>The current builder.</returns>
    public static IConfigurationBuilder AddEnvironmentVariables(
        this IConfigurationBuilder builder,
        Action<ConfigurationEnvironmentVariablesOptions> configureSource)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configureSource);

        var options = new ConfigurationEnvironmentVariablesOptions();

        configureSource.Invoke(options);

        return builder.AddProvider(_ => new ConfigurationEnvironmentVariablesProvider(options.Prefix));
    }
}
