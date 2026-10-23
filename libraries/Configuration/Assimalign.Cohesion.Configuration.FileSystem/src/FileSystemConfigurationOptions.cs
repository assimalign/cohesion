using System;
using System.IO;

namespace Assimalign.Cohesion.Configuration.FileSystem;

using Assimalign.Cohesion.FileSystem;

/// <summary>
/// Represents options used by file-backed configuration providers.
/// </summary>
[global::System.Diagnostics.DebuggerDisplay("FileSystemConfigurationOptions")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
public class FileSystemConfigurationOptions
{
    private TimeSpan _reloadDelay = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Gets or sets the file system used to resolve the configured file path.
    /// </summary>
    public IFileSystem? FileSystem { get; set; }

    /// <summary>
    /// Gets or sets the path to the configuration file within the configured file system.
    /// </summary>
    public FileSystemPath Path { get; set; } = FileSystemPath.Empty;

    /// <summary>
    /// Gets or sets a value that indicates whether the file is optional.
    /// </summary>
    public bool Optional { get; set; }

    /// <summary>
    /// Gets or sets a value that indicates whether the provider should reload when the file changes.
    /// </summary>
    public bool ReloadOnChange { get; set; }

    /// <summary>
    /// Gets or sets the debounce delay applied before a file change triggers a reload.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when the provided value is negative.
    /// </exception>
    public TimeSpan ReloadDelay
    {
        get => _reloadDelay;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, TimeSpan.Zero);
            _reloadDelay = value;
        }
    }

    /// <summary>
    /// Gets or sets the callback used to handle file load exceptions.
    /// </summary>
    public Action<ConfigurationFileLoadExceptionContext>? OnLoadException { get; set; }

    private sealed class DebugView
    {
        private readonly FileSystemConfigurationOptions _target;

        public DebugView(FileSystemConfigurationOptions value)
        {
            _target = value;
        }

        public IFileSystem? FileSystem => _target.FileSystem;
        public FileSystemPath Path => _target.Path;
        public bool Optional => _target.Optional;
        public bool ReloadOnChange => _target.ReloadOnChange;
        public TimeSpan ReloadDelay => _target.ReloadDelay;
        public Action<ConfigurationFileLoadExceptionContext>? OnLoadException => _target.OnLoadException;
    }
}
