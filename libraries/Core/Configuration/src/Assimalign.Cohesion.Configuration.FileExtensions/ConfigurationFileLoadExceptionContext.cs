using System;

namespace Assimalign.Extensions.Configuration.Providers;

/// <summary>
/// Contains information about a file load exception.
/// </summary>
public class ConfigurationFileLoadExceptionContext
{
    /// <summary>
    /// The <see cref="ConfigurationFileProvider"/> that caused the exception.
    /// </summary>
    public ConfigurationFileProvider Provider { get; set; }

    /// <summary>
    /// The exception that occurred in Load.
    /// </summary>
    public Exception Exception { get; set; }

    /// <summary>
    /// If true, the exception will not be rethrown.
    /// </summary>
    public bool Ignore { get; set; }
}
