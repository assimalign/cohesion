using System;
using System.IO;

namespace Assimalign.Cohesion.Configuration.Json;

using Assimalign.Cohesion.Configuration;
using Assimalign.Cohesion.Configuration.FileSystem;
using Assimalign.Cohesion.FileSystem;

/// <summary>
/// Adds JSON configuration provider registration helpers to configuration builders.
/// </summary>
public static class ConfigurationBuilderExtensions
{
    /// <summary>
    /// Adds a JSON configuration file to the builder.
    /// </summary>
    /// <param name="builder">The builder to add the provider to.</param>
    /// <param name="fileSystem">The file system used to resolve the JSON file.</param>
    /// <param name="path">The path to the JSON file.</param>
    /// <returns>The current builder.</returns>
    public static IConfigurationBuilder AddJsonFile(
        this IConfigurationBuilder builder,
        IFileSystem fileSystem,
        FileSystemPath path)
    {
        return builder.AddJsonFile(fileSystem, path, optional: false, reloadOnChange: false);
    }

    /// <summary>
    /// Adds a JSON configuration file to the builder with optional file behavior.
    /// </summary>
    /// <param name="builder">The builder to add the provider to.</param>
    /// <param name="fileSystem">The file system used to resolve the JSON file.</param>
    /// <param name="path">The path to the JSON file.</param>
    /// <param name="optional">A value that indicates whether the file is optional.</param>
    /// <returns>The current builder.</returns>
    public static IConfigurationBuilder AddJsonFile(
        this IConfigurationBuilder builder,
        IFileSystem fileSystem,
        FileSystemPath path,
        bool optional)
    {
        return builder.AddJsonFile(fileSystem, path, optional, reloadOnChange: false);
    }

    /// <summary>
    /// Adds a JSON configuration file to the builder.
    /// </summary>
    /// <param name="builder">The builder to add the provider to.</param>
    /// <param name="fileSystem">The file system used to resolve the JSON file.</param>
    /// <param name="path">The path to the JSON file.</param>
    /// <param name="optional">A value that indicates whether the file is optional.</param>
    /// <param name="reloadOnChange">A value that indicates whether the provider should reload when the file changes.</param>
    /// <returns>The current builder.</returns>
    public static IConfigurationBuilder AddJsonFile(
        this IConfigurationBuilder builder,
        IFileSystem fileSystem,
        FileSystemPath path,
        bool optional,
        bool reloadOnChange)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(fileSystem);

        if (path.IsEmpty)
        {
            throw new ArgumentException("The JSON configuration file path cannot be empty.", nameof(path));
        }

        return builder.AddJsonFile(options =>
        {
            options.FileSystem = fileSystem;
            options.Path = path;
            options.Optional = optional;
            options.ReloadOnChange = reloadOnChange;
        });
    }

    /// <summary>
    /// Adds a JSON configuration file to the builder using a configuration callback.
    /// </summary>
    /// <param name="builder">The builder to add the provider to.</param>
    /// <param name="configureOptions">The callback used to configure the JSON provider options.</param>
    /// <returns>The current builder.</returns>
    public static IConfigurationBuilder AddJsonFile(
        this IConfigurationBuilder builder,
        Action<ConfigurationJsonOptions> configureOptions)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configureOptions);

        return builder.AddFileSystemProvider(
            configureOptions,
            static options => new ConfigurationJsonProvider(options));
    }

    /// <summary>
    /// Adds a JSON configuration stream to the builder.
    /// </summary>
    /// <param name="builder">The builder to add the provider to.</param>
    /// <param name="stream">The JSON stream to read from.</param>
    /// <param name="leaveOpen">A value that indicates whether the stream should remain open when the provider is disposed.</param>
    /// <returns>The current builder.</returns>
    public static IConfigurationBuilder AddJsonStream(
        this IConfigurationBuilder builder,
        Stream stream,
        bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(stream);

        return builder.AddProvider(_ => new ConfigurationJsonStreamProvider(stream, leaveOpen));
    }
}
