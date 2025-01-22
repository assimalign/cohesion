using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading;

namespace Assimalign.Cohesion.Configuration.Providers;

using Assimalign.Cohesion;
using Assimalign.Cohesion.FileSystem;
using Assimalign.Cohesion.FileSystem.Globbing;
using Assimalign.Cohesion.System.IO;

/// <summary>
/// Base class for file based <see cref="ConfigurationProvider"/>.
/// </summary>
public abstract class ConfigurationFileProvider : ConfigurationProvider, IDisposable
{
    private readonly IDisposable _changeTokenRegistration;

    /// <summary>
    /// Initializes a new instance with the specified source.
    /// </summary>
    /// <param name="source">The source settings.</param>
    public ConfigurationFileProvider(ConfigurationFileSource source)
    {
        Source = source ?? throw new ArgumentNullException(nameof(source));

        if (Source.ReloadOnChange && Source.FileSytem != null)
        {
            _changeTokenRegistration = ChangeToken.OnChange(
                () => Source.FileSytem.Watch(Source.Path),
                () =>
                {
                    Thread.Sleep(Source.ReloadDelay);
                    Load(reload: true);
                });
        }
    }

    /// <summary>
    /// The source settings for this provider.
    /// </summary>
    public ConfigurationFileSource Source { get; }

    /// <summary>
    /// Generates a string representing this provider name and relevant details.
    /// </summary>
    /// <returns> The configuration name. </returns>
    public override string ToString()
        => $"{GetType().Name} for '{Source.Path}' ({(Source.Optional ? "Optional" : "Required")})";

    private void Load(bool reload)
    {
        var file = Source.FileSytem?.GetFile(Source.Path);
        if (file == null || !file.Exists)
        {
            if (Source.Optional || reload) // Always optional on reload
            {
                Data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
            else
            {
                var error = new StringBuilder("");// SR.Format(SR.Error_FileNotFound, Source.Path));
                if (!string.IsNullOrEmpty(file?.FullName))
                {
                    error.Append("not currently in the works");// SR.Format(SR.Error_ExpectedPhysicalPath, file.PhysicalPath));
                }
                HandleException(ExceptionDispatchInfo.Capture(new FileNotFoundException(error.ToString())));
            }
        }
        else
        {
            static Stream OpenRead(IFileSystemFile fileInfo)
            {
                if (fileInfo.FullName != null)
                {
                    // The default physical file info assumes asynchronous IO which results in unnecessary overhead
                    // especially since the configuration system is synchronous. This uses the same settings
                    // and disables async IO.
                    return new FileStream(
                        fileInfo.FullName,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite,
                        bufferSize: 1,
                        FileOptions.SequentialScan);
                }

                return fileInfo.CreateReadStream();
            }

            using (Stream stream = OpenRead(file))
            {
                try
                {
                    Load(stream);
                }
                catch (Exception ex)
                {
                    if (reload)
                    {
                        Data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    }
                    var exception = new InvalidDataException();// SR.Format(SR.Error_FailedToLoad, file.PhysicalPath), ex);
                    HandleException(ExceptionDispatchInfo.Capture(exception));
                }
            }
        }
        // REVIEW: Should we raise this in the base as well / instead?
        OnReload();
    }

    /// <summary>
    /// Loads the contents of the file at <see cref="Path"/>.
    /// </summary>
    /// <exception cref="DirectoryNotFoundException">If Optional is <c>false</c> on the source and part of a file or
    /// or directory cannot be found at the specified Path.</exception>
    /// <exception cref="FileNotFoundException">If Optional is <c>false</c> on the source and a
    /// file does not exist at specified Path.</exception>
    /// <exception cref="InvalidDataException">Wrapping any exception thrown by the concrete implementation of the
    /// <see cref="Load()"/> method. Use the source <see cref="ConfigurationFileSource.OnLoadException"/> callback
    /// if you need more control over the exception.</exception>
    public override void Load()
    {
        Load(reload: false);
    }

    /// <summary>
    /// Loads this provider's data from a stream.
    /// </summary>
    /// <param name="stream">The stream to read.</param>
    public abstract void Load(Stream stream);

    private void HandleException(ExceptionDispatchInfo info)
    {
        bool ignoreException = false;
        if (Source.OnLoadException != null)
        {
            var exceptionContext = new ConfigurationFileLoadExceptionContext
            {
                Provider = this,
                Exception = info.SourceException
            };
            Source.OnLoadException.Invoke(exceptionContext);
            ignoreException = exceptionContext.Ignore;
        }
        if (!ignoreException)
        {
            info.Throw();
        }
    }

    /// <inheritdoc />
    public void Dispose() => Dispose(true);

    /// <summary>
    /// Dispose the provider.
    /// </summary>
    /// <param name="disposing"><c>true</c> if invoked from <see cref="IDisposable.Dispose"/>.</param>
    protected virtual void Dispose(bool disposing)
    {
        _changeTokenRegistration?.Dispose();
    }
}
