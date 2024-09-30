using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace Assimalign.Cohesion.FileSystem;

using Internal;


/// <summary>
/// Looks up files using the on-disk file system
/// </summary>
/// <remarks>
/// When the environment variable "DOTNET_USE_POLLING_FILE_WATCHER" is set to "1" or "true", calls to
/// <see cref="Watch(string)" /> will use <see cref="PollingFileChangeToken" />.
/// </remarks>
public class PhysicalFileProvider : IFileSystemProvider, IDisposable
{
    private const string PollingEnvironmentKey = "DOTNET_USE_POLLING_FILE_WATCHER";
    private static readonly char[] separators = new[] {Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar};

    private readonly ExclusionFilterType _filters;

    private readonly Func<PhysicalFilesWatcher> fileWatcherFactory;
    private PhysicalFilesWatcher fileWatcher;
    private bool fileWatcherInitialized;
    private object fileWatcherLock = new object();

    private bool? usePollingFileWatcher;
    private bool? useActivePolling;
    private bool isDisposed;

    /// <summary>
    /// Initializes a new instance of a PhysicalFileProvider at the given root directory.
    /// </summary>
    /// <param name="root">The root directory. This should be an absolute path.</param>
    public PhysicalFileProvider(string root) : this(root, ExclusionFilterType.Sensitive) { }

    /// <summary>
    /// Initializes a new instance of a PhysicalFileProvider at the given root directory.
    /// </summary>
    /// <param name="root">The root directory. This should be an absolute path.</param>
    /// <param name="filters">Specifies which files or directories are excluded.</param>
    public PhysicalFileProvider(string root, ExclusionFilterType filters)
    {
        if (!Path.IsPathRooted(root))
        {
            throw new ArgumentException("The path must be absolute.", nameof(root));
        }

        string fullRoot = Path.GetFullPath(root);
        // When we do matches in GetFullPath, we want to only match full directory names.
        Root = PathUtilities.EnsureTrailingSlash(fullRoot);
        if (!Directory.Exists(Root))
        {
            throw new DirectoryNotFoundException(Root);
        }

        _filters = filters;
        fileWatcherFactory = () => CreateFileWatcher();
    }

    /// <summary>
    /// Gets or sets a value that determines if this instance of <see cref="PhysicalFileProvider"/>
    /// uses polling to determine file changes.
    /// <para>
    /// By default, <see cref="PhysicalFileProvider"/>  uses <see cref="FileSystemWatcher"/> to listen to file change events
    /// for <see cref="Watch(string)"/>. <see cref="FileSystemWatcher"/> is ineffective in some scenarios such as mounted drives.
    /// Polling is required to effectively watch for file changes.
    /// </para>
    /// <seealso cref="UseActivePolling"/>.
    /// </summary>
    /// <value>
    /// The default value of this property is determined by the value of environment variable named <c>DOTNET_USE_POLLING_FILE_WATCHER</c>.
    /// When <c>true</c> or <c>1</c>, this property defaults to <c>true</c>; otherwise false.
    /// </value>
    public bool UsePollingFileWatcher
    {
        get
        {
            if (fileWatcher != null)
            {
                return false;
            }
            if (usePollingFileWatcher == null)
            {
                ReadPollingEnvironmentVariables();
            }
            return usePollingFileWatcher ?? false;
        }
        set
        {
            if (fileWatcher != null)
            {
                throw new InvalidOperationException();// SR.Format(SR.CannotModifyWhenFileWatcherInitialized, nameof(UsePollingFileWatcher)));
            }
            usePollingFileWatcher = value;
        }
    }

    /// <summary>
    /// Gets or sets a value that determines if this instance of <see cref="PhysicalFileProvider"/>
    /// actively polls for file changes.
    /// <para>
    /// When <see langword="true"/>, <see cref="IChangeToken"/> returned by <see cref="Watch(string)"/> will actively poll for file changes
    /// (<see cref="IChangeToken.ActiveChangeCallbacks"/> will be <see langword="true"/>) instead of being passive.
    /// </para>
    /// <para>
    /// This property is only effective when <see cref="UsePollingFileWatcher"/> is set.
    /// </para>
    /// </summary>
    /// <value>
    /// The default value of this property is determined by the value of environment variable named <c>DOTNET_USE_POLLING_FILE_WATCHER</c>.
    /// When <c>true</c> or <c>1</c>, this property defaults to <c>true</c>; otherwise false.
    /// </value>
    public bool UseActivePolling
    {
        get
        {
            if (useActivePolling == null)
            {
                ReadPollingEnvironmentVariables();
            }

            return useActivePolling.Value;
        }

        set => useActivePolling = value;
    }

    /// <summary>
    /// The root directory for this instance.
    /// </summary>
    public string Root { get; }


    internal PhysicalFilesWatcher FileWatcher
    {
        get
        {
            return LazyInitializer.EnsureInitialized(
                ref fileWatcher,
                ref fileWatcherInitialized,
                ref fileWatcherLock,
                fileWatcherFactory);
        }
        set
        {
            Debug.Assert(!fileWatcherInitialized);

            fileWatcherInitialized = true;
            fileWatcher = value;
        }
    }

    internal PhysicalFilesWatcher CreateFileWatcher()
    {
        string root = PathUtilities.EnsureTrailingSlash(Path.GetFullPath(Root));

        FileSystemWatcher watcher;

        //  For browser/iOS/tvOS we will proactively fallback to polling since FileSystemWatcher is not supported.
        if (OperatingSystem.IsBrowser() || (OperatingSystem.IsIOS() && !OperatingSystem.IsMacCatalyst()) || OperatingSystem.IsTvOS())
        {
            UsePollingFileWatcher = true;
            UseActivePolling = true;
            watcher = null;
        }
        else
        {
            // When UsePollingFileWatcher & UseActivePolling are set, we won't use a FileSystemWatcher.
            watcher = UsePollingFileWatcher && UseActivePolling ? null : new FileSystemWatcher(root);
        }

        return new PhysicalFilesWatcher(root, watcher, UsePollingFileWatcher, _filters)
        {
            UseActivePolling = UseActivePolling,
        };
    }

    private void ReadPollingEnvironmentVariables()
    {
        string environmentValue = Environment.GetEnvironmentVariable(PollingEnvironmentKey);
        bool pollForChanges = string.Equals(environmentValue, "1", StringComparison.Ordinal) ||
            string.Equals(environmentValue, "true", StringComparison.OrdinalIgnoreCase);

        usePollingFileWatcher = pollForChanges;
        useActivePolling = pollForChanges;
    }

    /// <summary>
    /// Disposes the provider. Change tokens may not trigger after the provider is disposed.
    /// </summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Disposes the provider.
    /// </summary>
    /// <param name="disposing"><c>true</c> is invoked from <see cref="IDisposable.Dispose"/>.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (!isDisposed)
        {
            if (disposing)
            {
                fileWatcher?.Dispose();
            }
            isDisposed = true;
        }
    }

    
    private string GetFullPath(string path)
    {
        if (PathUtilities.PathNavigatesAboveRoot(path))
        {
            return null;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(Path.Combine(Root, path));
        }
        catch
        {
            return null;
        }

        if (!IsUnderneathRoot(fullPath))
        {
            return null;
        }

        return fullPath;
    }

    private bool IsUnderneathRoot(string fullPath)
    {
        return fullPath.StartsWith(Root, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Locate a file at the given path by directly mapping path segments to physical directories.
    /// </summary>
    /// <param name="subpath">A path under the root directory</param>
    /// <returns>The file information. Caller must check <see cref="IFileInfo.Exists"/> property. </returns>
    public IFileSystemInfo GetFile(string subpath)
    {
        if (string.IsNullOrEmpty(subpath) || PathUtilities.HasInvalidPathChars(subpath))
        {
            return null;
        }

        // Relative paths starting with leading slashes are okay
        subpath = subpath.TrimStart(separators);

        // Absolute paths not permitted.
        if (Path.IsPathRooted(subpath))
        {
            return null;
        }

        string fullPath = GetFullPath(subpath);
        if (fullPath == null)
        {
            return null;
        }

        var fileInfo = new FileInfo(fullPath);
        if (FileSystemInfoHelper.IsExcluded(fileInfo, _filters))
        {
            return null;
        }

        return new PhysicalFileInfo(fileInfo);
    }

    /// <summary>
    /// Enumerate a directory at the given path, if any.
    /// </summary>
    /// <param name="subpath">A path under the root directory. Leading slashes are ignored.</param>
    /// <returns>
    /// Contents of the directory. Caller must check <see cref="IFileSystemDirectoryContent.Exists"/> property. <see cref="NotFoundDirectoryContents" /> if
    /// <paramref name="subpath" /> is absolute, if the directory does not exist, or <paramref name="subpath" /> has invalid
    /// characters.
    /// </returns>
    public IFileSystemDirectoryInfo GetDirectory(string subpath)
    {
        try
        {
            if (subpath == null || PathUtilities.HasInvalidPathChars(subpath))
            {
                return null;
            }

            // Relative paths starting with leading slashes are okay
            subpath = subpath.TrimStart(separators);

            // Absolute paths not permitted.
            if (Path.IsPathRooted(subpath))
            {
                return null;
            }

            string fullPath = GetFullPath(subpath);
            if (fullPath == null || !Directory.Exists(fullPath))
            {
                return null;
            }

            return new PhysicalFileSystemDirectory(fullPath, _filters);
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (IOException)
        {
        }
        return null;
    }

    /// <summary>
    ///     <para>Creates a <see cref="IChangeToken" /> for the specified <paramref name="filter" />.</para>
    ///     <para>Globbing patterns are interpreted by <seealso cref="Assimalign.Cohesion.FileSystemGlobbing.FilePatternMatcher" />.</para>
    /// </summary>
    /// <param name="filter">
    /// Filter string used to determine what files or folders to monitor. Example: **/*.cs, *.*,
    /// subFolder/**/*.cshtml.
    /// </param>
    /// <returns>
    /// An <see cref="IChangeToken" /> that is notified when a file matching <paramref name="filter" /> is added,
    /// modified or deleted. Returns a <see cref="NullChangeToken" /> if <paramref name="filter" /> has invalid filter
    /// characters or if <paramref name="filter" /> is an absolute path or outside the root directory specified in the
    /// constructor <seealso cref="PhysicalFileProvider(string)" />.
    /// </returns>
    public IChangeToken Watch(string filter)
    {
        if (filter is null)
        {
            throw new ArgumentNullException(nameof(filter));
        }
        if (PathUtilities.HasInvalidFilterChars(filter))
        {
            throw new ArgumentException("The provider filter has an invalid character.");
        }

        // Relative paths starting with leading slashes are okay
        filter = filter.TrimStart(separators);

        return FileWatcher.CreateFileChangeToken(filter);
    }

    IFileSystemDirectory IFileSystemProvider.GetDirectory(string subpath)
    {
        throw new NotImplementedException();
    }
}
