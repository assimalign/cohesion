using System;
using System.Globalization;
using System.Text;
using Assimalign.Cohesion.Logging.Debug.Internal;

namespace Assimalign.Cohesion.Logging.Debug;

/// <summary>
/// <see cref="ILoggerProvider"/> that writes structured log entries through
/// <see cref="System.Diagnostics.Debug.WriteLine(string)"/> (or a caller-supplied writer).
/// </summary>
/// <remarks>
/// The provider only emits while a debugger is attached unless
/// <see cref="DebugLoggerOptions.EmitOnlyWhenDebuggerAttached"/> is set to <see langword="false"/>.
/// Output is line-oriented so it surfaces nicely in Visual Studio's Output window or any other
/// <c>Debug</c> listener.
/// </remarks>
public sealed class DebugLoggerProvider : LoggerProvider
{
    private readonly DebugLoggerOptions _options;

    /// <summary>
    /// Initializes a provider with default options.
    /// </summary>
    public DebugLoggerProvider()
        : this(new DebugLoggerOptions())
    {
    }

    /// <summary>
    /// Initializes a provider with the supplied options.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    public DebugLoggerProvider(DebugLoggerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <inheritdoc />
    public override string Name => "Debug";

    /// <inheritdoc />
    protected override Logger CreateCore(string category) => new DebugLogger(category, this);

    internal bool IsEnabledFor(LogLevel level)
    {
        if (IsDisposed || level == LogLevel.None)
        {
            return false;
        }

        if (_options.EmitOnlyWhenDebuggerAttached && _options.Writer is null && !System.Diagnostics.Debugger.IsAttached)
        {
            return false;
        }

        return true;
    }

    internal void Write(ILoggerEntry entry)
    {
        if (!IsEnabledFor(entry.Level))
        {
            return;
        }

        var builder = new StringBuilder(capacity: 256);
        builder.Append('[');
        builder.Append(entry.Timestamp.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        builder.Append("] [");
        builder.Append(LevelToString(entry.Level));
        builder.Append("] ");
        builder.Append(entry.Category);
        builder.Append(": ");
        builder.Append(entry.Message);

        if (_options.IncludeAttributes && entry.Attributes.Count > 0)
        {
            builder.Append(" {");
            bool first = true;
            foreach (var pair in entry.Attributes)
            {
                if (!first)
                {
                    builder.Append(", ");
                }
                builder.Append(pair.Key);
                builder.Append('=');
                builder.Append(pair.Value is null ? "null" : pair.Value.ToString());
                first = false;
            }
            builder.Append('}');
        }

        var line = builder.ToString();

        if (_options.Writer is { } writer)
        {
            try { writer(line); } catch { }
        }
        else
        {
            System.Diagnostics.Debug.WriteLine(line);
        }

        if (_options.IncludeException && entry.Exception is { } exception)
        {
            if (_options.Writer is { } writer2)
            {
                try { writer2(exception.ToString()); } catch { }
            }
            else
            {
                System.Diagnostics.Debug.WriteLine(exception);
            }
        }
    }

    private static string LevelToString(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRACE",
        LogLevel.Debug => "DEBUG",
        LogLevel.Information => "INFO",
        LogLevel.Warning => "WARN",
        LogLevel.Error => "ERROR",
        LogLevel.Critical => "CRIT",
        LogLevel.Event => "EVENT",
        LogLevel.None => "NONE",
        _ => level.ToString().ToUpperInvariant(),
    };
}
