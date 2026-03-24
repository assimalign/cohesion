using System;

namespace Assimalign.Cohesion.Configuration.FileSystem;

/// <summary>
/// Contains information about a file-backed configuration load failure.
/// </summary>
public sealed class ConfigurationFileLoadExceptionContext
{
    /// <summary>
    /// Gets the provider that encountered the load failure.
    /// </summary>
    public required FileSystemConfigurationProvider Provider { get; init; }

    /// <summary>
    /// Gets the exception that occurred while loading the file.
    /// </summary>
    public required Exception Exception { get; init; }

    /// <summary>
    /// Gets or sets a value that indicates whether the exception should be ignored.
    /// </summary>
    public bool Ignore { get; set; }
}
