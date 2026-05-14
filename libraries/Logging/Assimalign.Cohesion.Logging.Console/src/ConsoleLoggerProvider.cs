using System;
using System.IO;
using System.Threading;
using Assimalign.Cohesion.Logging.Internal;

namespace Assimalign.Cohesion.Logging;

/// <summary>
/// <see cref="ILoggerProvider"/> that writes structured log entries to a pair of
/// <see cref="TextWriter"/>s (one for general output, one for error-level output).
/// </summary>
/// <remarks>
/// The provider is thread-safe. Output is serialized through an internal lock so concurrent
/// log entries do not interleave on the same writer.
/// </remarks>
public sealed class ConsoleLoggerProvider : LoggerProvider
{
    private readonly ConsoleLoggerOptions _options;
    private readonly Lock _writeLock = new();

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
    public override string Name => "Console";

    /// <inheritdoc />
    protected override Logger CreateCore(string category) => new ConsoleLogger(category, this);

    /// <inheritdoc />
    protected override void DisposeCore()
    {
        // Flush whatever writers the caller supplied; never close them - they may be shared
        // with the rest of the host (System.Console.Out cannot be closed without breaking the
        // process).
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
