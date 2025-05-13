using System;
using System.IO;

namespace Assimalign.Cohesion.Configuration;

using Assimalign.Cohesion.Configuration;
using Assimalign.Cohesion.FileSystem;

/// <summary>
/// Represents a base class for file based <see cref="IConfigurationSource"/>.
/// </summary>
public sealed class FileSystemConfigurationOptions
{
    /// <summary>
    /// Used to access the contents of the file.
    /// </summary>
    public IFileSystem FileSystem { get; set; }

    /// <summary>
    /// The path to the file.
    /// </summary>
    public FileSystemPath Path { get; set; }

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


}
