using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Configuration;

namespace Assimalign.Cohesion.Configuration.CommandLine;

/// <summary>
/// Adds command-line provider registration helpers to configuration builders.
/// </summary>
public static class ConfigurationBuilderExtensions
{
    /// <summary>
    /// Adds a command-line configuration provider using the supplied arguments.
    /// </summary>
    /// <param name="builder">The builder to add to.</param>
    /// <param name="args">The command-line arguments to parse.</param>
    /// <returns>The current builder.</returns>
    public static IConfigurationBuilder AddCommandLine(this IConfigurationBuilder builder, string[] args)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(args);

        return builder.AddCommandLine(args, switchMappings: null);
    }

    /// <summary>
    /// Adds a command-line configuration provider using the supplied arguments and switch mappings.
    /// </summary>
    /// <param name="builder">The builder to add to.</param>
    /// <param name="args">The command-line arguments to parse.</param>
    /// <param name="switchMappings">Optional switch mappings for short and aliased switches.</param>
    /// <returns>The current builder.</returns>
    public static IConfigurationBuilder AddCommandLine(
        this IConfigurationBuilder builder,
        string[] args,
        IDictionary<string, string>? switchMappings)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(args);

        return builder.AddProvider(_ => new ConfigurationCommandLineProvider(args, switchMappings));
    }

    /// <summary>
    /// Adds a command-line configuration provider using a configuration callback.
    /// </summary>
    /// <param name="builder">The builder to add to.</param>
    /// <param name="configureOptions">The callback used to configure provider options.</param>
    /// <returns>The current builder.</returns>
    public static IConfigurationBuilder AddCommandLine(
        this IConfigurationBuilder builder,
        Action<ConfigurationCommandLineOptions> configureOptions)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configureOptions);

        var options = new ConfigurationCommandLineOptions();
        configureOptions.Invoke(options);

        return builder.AddProvider(_ => new ConfigurationCommandLineProvider(options.Args, options.SwitchMappings));
    }
}
