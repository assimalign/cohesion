﻿using System;
using System.IO;

namespace Assimalign.Cohesion.Configuration.Providers;

using Assimalign.Cohesion.Configuration;
using Assimalign.Cohesion.FileSystem;
using Assimalign.Cohesion.System.IO;

/// <summary>
/// Represents a base class for file based <see cref="IConfigurationSource"/>.
/// </summary>
public abstract class ConfigurationFileSource : IConfigurationSource
{
    /// <summary>
    /// Used to access the contents of the file.
    /// </summary>
    public IFileSystem FileSytem { get; set; }

    /// <summary>
    /// The path to the file.
    /// </summary>
    public Path Path { get; set; }

    /// <summary>
    /// Determines if loading the file is optional.
    /// </summary>
    public bool Optional { get; set; }

    /// <summary>
    /// Determines whether the source will be loaded if the underlying file changes.
    /// </summary>
    public bool ReloadOnChange { get; set; }

    /// <summary>
    /// Number of milliseconds that reload will wait before calling Load.  This helps
    /// avoid triggering reload before a file is completely written. Default is 250.
    /// </summary>
    public int ReloadDelay { get; set; } = 250;

    /// <summary>
    /// Will be called if an uncaught exception occurs in FileConfigurationProvider.Load.
    /// </summary>
    public Action<ConfigurationFileLoadExceptionContext> OnLoadException { get; set; }

    /// <summary>
    /// Builds the <see cref="IConfigurationProvider"/> for this source.
    /// </summary>
    /// <param name="builder">The <see cref="IConfigurationBuilder"/>.</param>
    /// <returns>A <see cref="IConfigurationProvider"/></returns>
    public abstract IConfigurationProvider Build(IConfigurationBuilder builder);

    /// <summary>
    /// Called to use any default settings on the builder like the FileProvider or FileLoadExceptionHandler.
    /// </summary>
    /// <param name="builder">The <see cref="IConfigurationBuilder"/>.</param>
    public void EnsureDefaults(IConfigurationBuilder builder)
    {
        FileSytem = FileSytem ?? builder.GetFileProvider();
        OnLoadException = OnLoadException ?? builder.GetFileLoadExceptionHandler();
    }

    /// <summary>
    /// If no file provider has been set, for absolute Path, this will creates a physical file provider
    /// for the nearest existing directory.
    /// </summary>
    public void ResolveFileProvider()
    {
        if (FileSytem == null &&
            !string.IsNullOrEmpty(Path) &&
            System.IO.Path.IsPathRooted(Path))
        {
            string directory = System.IO.Path.GetDirectoryName(Path);
            string pathToFile = System.IO.Path.GetFileName(Path);
            while (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                pathToFile = System.IO.Path.Combine(System.IO.Path.GetFileName(directory), pathToFile);
                directory = System.IO.Path.GetDirectoryName(directory);
            }
            if (Directory.Exists(directory))
            {
                FileSytem = new PhysicalFileProvider(directory);
                Path = pathToFile;
            }
        }
    }

}
