using System;
using System.IO;
using System.Threading;
using Assimalign.Cohesion.Logging.Console.Internal;

namespace Assimalign.Cohesion.Logging.Console;

/// <summary>
/// <see cref="ILoggerProvider"/> that writes structured log entries to a pair of
/// <see cref="TextWriter"/>s (one for general output, one for error-level output).
/// </summary>
/// <remarks>
/// The provider is thread-safe. Output is serialized through an internal lock so concurrent
/// log entries do not interleave on the same writer.
/// </remarks>
public sealed class ConsoleLoggerProvider : ILoggerProvider
{
    private readonly ConsoleLoggerOptions _options;
    private readonly Lock _writeLock = new();
    private int _disposed;

    /// <summary>
    /// Initializes a provider with default options.
    /// </summary>
    public ConsoleLoggerProvider()
        : this(new ConsoleLoggerOptions())
    {
    }

    /// <summary>
    /// Initializes a provider with the supplied options.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    public ConsoleLoggerProvider(ConsoleLoggerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <inheritdoc />
    public string Name => "Console";

    internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <inheritdoc />
    public ILogger Create(string category)
    {
        ArgumentException.ThrowIfNullOrEmpty(category);

        if (IsDisposed)
        {
            throw new ObjectDisposedException(nameof(ConsoleLoggerProvider));
        }

        return new ConsoleLogger(category, this);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // Flush whatever writers the caller supplied; never close them - they may be shared with
        // the rest of the host (System.Console.Out cannot be closed without breaking the process).
        lock (_writeLock)
        {
            try { _options.Output?.Flush(); } catch { }
            try { _options.ErrorOutput?.Flush(); } catch { }
        }
    }

    internal void Write(ILoggerEntry entry)
    {
        if (IsDisposed)
        {
            return;
        }

        var writer = entry.Level >= LogLevel.Error
            ? (_options.ErrorOutput ?? System.Console.Error)
            : (_options.Output ?? System.Console.Out);

        lock (_writeLock)
        {
            if (IsDisposed)
            {
                return;
            }

            if (_options.Formatter is { } formatter)
            {
                try
                {
                    formatter(entry, writer);
                }
                catch
                {
                    // Custom formatter exceptions must not bring down the provider.
                }
            }
            else
            {
                ConsoleLogFormatter.Write(entry, writer, _options);
            }

            writer.Flush();
        }
    }
}
