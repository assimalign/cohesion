using System;

namespace Assimalign.Cohesion.Configuration.FileSystem;

using Assimalign.Cohesion.Configuration;

/// <summary>
/// Adds shared registration helpers for file-backed configuration providers.
/// </summary>
public static class ConfigurationBuilderExtensions
{
    /// <summary>
    /// Adds a file-backed configuration provider that is configured through a typed options callback.
    /// </summary>
    /// <typeparam name="TOptions">The typed options used to configure the provider.</typeparam>
    /// <param name="builder">The builder to add the provider to.</param>
    /// <param name="configureOptions">The callback used to configure the provider options.</param>
    /// <param name="providerFactory">The factory used to create the provider from the configured options.</param>
    /// <returns>The current builder.</returns>
    public static IConfigurationBuilder AddFileSystemProvider<TOptions>(
        this IConfigurationBuilder builder,
        Action<TOptions> configureOptions,
        Func<TOptions, IConfigurationProvider> providerFactory)
        where TOptions : FileSystemConfigurationOptions, new()
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configureOptions);
        ArgumentNullException.ThrowIfNull(providerFactory);

        var options = new TOptions();
        configureOptions.Invoke(options);

        return builder.AddProvider(_ => providerFactory.Invoke(options));
    }
}
