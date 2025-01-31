﻿using System;

namespace Assimalign.Cohesion.Configuration;

using Assimalign.Cohesion.FileProviders;
using Assimalign.Cohesion.FileProviders.Physical;
using Assimalign.Cohesion.Configuration.Providers;
using Assimalign.Cohesion.FileSystem;

public static partial class ConfigurationBuilderExtensions
{
    #region File Provider
    private static string FileProviderKey = "FileProvider";
    private static string FileLoadExceptionHandlerKey = "FileLoadExceptionHandler";

    /// <summary>
    /// Sets the default <see cref="IFileProvider"/> to be used for file-based providers.
    /// </summary>
    /// <param name="builder">The <see cref="IConfigurationBuilder"/> to add to.</param>
    /// <param name="fileProvider">The default file provider instance.</param>
    /// <returns>The <see cref="IConfigurationBuilder"/>.</returns>
    public static IConfigurationBuilder SetFileProvider(this IConfigurationBuilder builder, IFileProvider fileProvider)
    {
        if (builder == null)
        {
            throw new ArgumentNullException(nameof(builder));
        }

        builder.Properties[FileProviderKey] = fileProvider ?? throw new ArgumentNullException(nameof(fileProvider));
        return builder;
    }

    /// <summary>
    /// Gets the default <see cref="IFileProvider"/> to be used for file-based providers.
    /// </summary>
    /// <param name="builder">The <see cref="IConfigurationBuilder"/>.</param>
    /// <returns>The default <see cref="IFileProvider"/>.</returns>
    public static IFileProvider GetFileProvider(this IConfigurationBuilder builder)
    {
        if (builder == null)
        {
            throw new ArgumentNullException(nameof(builder));
        }

        if (builder.Properties.TryGetValue(FileProviderKey, out object provider))
        {
            return provider as IFileProvider;
        }

        return new PhysicalFileProvider(AppContext.BaseDirectory ?? string.Empty);
    }

    /// <summary>
    /// Sets the FileProvider for file-based providers to a PhysicalFileProvider with the base path.
    /// </summary>
    /// <param name="builder">The <see cref="IConfigurationBuilder"/> to add to.</param>
    /// <param name="basePath">The absolute path of file-based providers.</param>
    /// <returns>The <see cref="IConfigurationBuilder"/>.</returns>
    public static IConfigurationBuilder SetBasePath(this IConfigurationBuilder builder, string basePath)
    {
        if (builder == null)
        {
            throw new ArgumentNullException(nameof(builder));
        }

        if (basePath == null)
        {
            throw new ArgumentNullException(nameof(basePath));
        }

        return builder.SetFileProvider(new PhysicalFileProvider(basePath));
    }

    /// <summary>
    /// Sets a default action to be invoked for file-based providers when an error occurs.
    /// </summary>
    /// <param name="builder">The <see cref="IConfigurationBuilder"/> to add to.</param>
    /// <param name="handler">The Action to be invoked on a file load exception.</param>
    /// <returns>The <see cref="IConfigurationBuilder"/>.</returns>
    public static IConfigurationBuilder SetFileLoadExceptionHandler(this IConfigurationBuilder builder, Action<ConfigurationFileLoadExceptionContext> handler)
    {
        if (builder == null)
        {
            throw new ArgumentNullException(nameof(builder));
        }

        builder.Properties[FileLoadExceptionHandlerKey] = handler;
        return builder;
    }

    /// <summary>
    /// Gets the default <see cref="IFileProvider"/> to be used for file-based providers.
    /// </summary>
    /// <param name="builder">The <see cref="IConfigurationBuilder"/>.</param>
    /// <returns>The <see cref="IConfigurationBuilder"/>.</returns>
    public static Action<ConfigurationFileLoadExceptionContext> GetFileLoadExceptionHandler(this IConfigurationBuilder builder)
    {
        if (builder == null)
        {
            throw new ArgumentNullException(nameof(builder));
        }

        if (builder.Properties.TryGetValue(FileLoadExceptionHandlerKey, out object handler))
        {
            return handler as Action<ConfigurationFileLoadExceptionContext>;
        }
        return null;
    }
    #endregion


    public static IConfigurationBuilder AddConfigurationFile(this IConfigurationBuilder builder, Func<IFileSystem>)
    {

    }
}
