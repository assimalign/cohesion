using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Configuration.FileSystem;

using Assimalign.Cohesion.Configuration;
using Assimalign.Cohesion.FileSystem;

/// <summary>
/// Provides a shared base implementation for configuration providers that load values from an <see cref="IFileSystem"/>.
/// </summary>
public abstract class FileSystemConfigurationProvider : ConfigurationProvider
{
    private readonly Lock _reloadLock = new();
    private readonly IDisposable[] _changeRegistrations;
    private readonly FileSystemPath _absolutePath;

    private CancellationTokenSource? _reloadCancellationTokenSource;
    private bool _isDisposed;
    private bool _isReloading;

    /// <summary>
    /// Initializes a new file-backed configuration provider.
    /// </summary>
    /// <param name="options">The options used to configure file loading and reload behavior.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="options"/> or its configured file system is null.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when the configured file path is empty.
    /// </exception>
    protected FileSystemConfigurationProvider(FileSystemConfigurationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.FileSystem);

        if (options.Path.IsEmpty)
        {
            throw new ArgumentException("The file system configuration path cannot be empty.", nameof(options));
        }

        Options = options;
        _absolutePath = options.FileSystem.RootDirectory.Path.Merge(options.Path);
        _changeRegistrations = options.ReloadOnChange
            ? RegisterChangeHandlers(options.FileSystem.Watch(Glob.Parse(_absolutePath.ToString())))
            : [];
    }

    /// <summary>
    /// Gets the configured file-backed provider options.
    /// </summary>
    protected FileSystemConfigurationOptions Options { get; }

    /// <summary>
    /// Gets the configured file system.
    /// </summary>
    protected IFileSystem FileSystem => Options.FileSystem!;

    /// <summary>
    /// Gets the configured file path relative to the file system root.
    /// </summary>
    protected FileSystemPath FilePath => Options.Path;

    /// <summary>
    /// Gets the absolute path used for change watching.
    /// </summary>
    protected FileSystemPath AbsolutePath => _absolutePath;

    /// <summary>
    /// Reads entries from the provided file stream.
    /// </summary>
    /// <param name="stream">The file stream to read from.</param>
    /// <param name="entries">The output entry collection to populate.</param>
    /// <param name="cancellationToken">The cancellation token used to cancel the load.</param>
    /// <returns>A task that completes when the stream has been read.</returns>
    protected abstract Task ReadAsync(
        Stream stream,
        IDictionary<Path, string?> entries,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens a read stream for the specified file.
    /// </summary>
    /// <param name="file">The file to open for reading.</param>
    /// <returns>The opened read stream.</returns>
    protected virtual Stream OpenRead(IFileSystemFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        return file.Open(FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
    }

    /// <inheritdoc />
    protected sealed override async Task OnLoadAsync(
        IDictionary<Path, string?> entries,
        CancellationToken cancellationToken = default)
    {
        if (!FileSystem.Exists(FilePath))
        {
            if (Options.Optional || _isReloading)
            {
                return;
            }

            Throw(new FileNotFoundException(
                $"The configuration file '{FilePath}' was not found."));
        }

        try
        {
            IFileSystemFile file = FileSystem.GetFile(FilePath);

            using Stream stream = OpenRead(file);

            await ReadAsync(stream, entries, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            HandleException(exception, allowThrow: !_isReloading);
        }
    }

    /// <inheritdoc />
    protected override ValueTask OnDisposeAsync(IEnumerable<IConfigurationEntry> entries)
    {
        _isDisposed = true;

        lock (_reloadLock)
        {
            if (_reloadCancellationTokenSource is not null)
            {
                _reloadCancellationTokenSource.Cancel();
                _reloadCancellationTokenSource.Dispose();
                _reloadCancellationTokenSource = null;
            }
        }

        for (int index = 0; index < _changeRegistrations.Length; index++)
        {
            _changeRegistrations[index].Dispose();
        }

        return ValueTask.CompletedTask;
    }

    private IDisposable[] RegisterChangeHandlers(IFileSystemEventToken changeToken)
    {
        ArgumentNullException.ThrowIfNull(changeToken);

        var registrations = new List<IDisposable>(5)
        {
            changeToken.OnChange(static state => ((FileSystemConfigurationProvider)state!).QueueReload(), this),
            changeToken.OnCreate(static args => args.State!.QueueReload(), this),
            changeToken.OnDelete(static args => args.State!.QueueReload(), this),
            changeToken.OnRename(static args => args.State!.QueueReload(), this),
        };

        if (changeToken is IDisposable disposable)
        {
            registrations.Add(disposable);
        }

        return [.. registrations];
    }

    private void QueueReload()
    {
        CancellationTokenSource reloadCancellationTokenSource;

        lock (_reloadLock)
        {
            if (_isDisposed)
            {
                return;
            }

            _reloadCancellationTokenSource?.Cancel();
            _reloadCancellationTokenSource?.Dispose();

            reloadCancellationTokenSource = new CancellationTokenSource();
            _reloadCancellationTokenSource = reloadCancellationTokenSource;
        }

        _ = ReloadAfterDelayAsync(reloadCancellationTokenSource);
    }

    private async Task ReloadAfterDelayAsync(CancellationTokenSource reloadCancellationTokenSource)
    {
        try
        {
            if (Options.ReloadDelay > TimeSpan.Zero)
            {
                await Task.Delay(Options.ReloadDelay, reloadCancellationTokenSource.Token).ConfigureAwait(false);
            }

            _isReloading = true;

            await base.LoadAsync(reloadCancellationTokenSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (reloadCancellationTokenSource.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            HandleException(exception, allowThrow: false);
        }
        finally
        {
            _isReloading = false;

            lock (_reloadLock)
            {
                if (ReferenceEquals(_reloadCancellationTokenSource, reloadCancellationTokenSource))
                {
                    _reloadCancellationTokenSource.Dispose();
                    _reloadCancellationTokenSource = null;
                }
            }
        }
    }

    private void HandleException(Exception exception, bool allowThrow)
    {
        bool ignoreException = false;

        if (Options.OnLoadException is not null)
        {
            var context = new ConfigurationFileLoadExceptionContext
            {
                Provider = this,
                Exception = exception,
            };

            Options.OnLoadException.Invoke(context);
            ignoreException = context.Ignore;
        }

        if (!allowThrow || ignoreException)
        {
            return;
        }

        Throw(exception);
    }

    private static void Throw(Exception exception)
    {
        ExceptionDispatchInfo.Capture(exception).Throw();
    }
}
