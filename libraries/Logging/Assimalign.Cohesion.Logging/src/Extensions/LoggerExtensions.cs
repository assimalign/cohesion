using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Logging;

/// <summary>
/// Ergonomic typed-level helpers over <see cref="ILogger"/>.
/// </summary>
/// <remarks>
/// The helpers short-circuit on <see cref="ILogger.IsEnabled(LogLevel)"/> so they do not allocate
/// an entry when no underlying sink would accept it. Callers that already know they are enabled
/// can build a <see cref="LoggerEntryBuilder"/> directly.
/// </remarks>
public static class LoggerExtensions
{
    extension(ILogger logger)
    {
        /// <summary>
        /// Writes a trace-level entry under <paramref name="category"/>.
        /// </summary>
        public void LogTrace(string category, string message, IReadOnlyDictionary<string, object?>? attributes = null)
        {
            Write(logger, LogLevel.Trace, category, message, exception: null, attributes);
        }

        /// <summary>
        /// Writes a debug-level entry under <paramref name="category"/>.
        /// </summary>
        public void LogDebug(string category, string message, IReadOnlyDictionary<string, object?>? attributes = null)
        {
            Write(logger, LogLevel.Debug, category, message, exception: null, attributes);
        }

        /// <summary>
        /// Writes an information-level entry under <paramref name="category"/>.
        /// </summary>
        public void LogInformation(string category, string message, IReadOnlyDictionary<string, object?>? attributes = null)
        {
            Write(logger, LogLevel.Information, category, message, exception: null, attributes);
        }

        /// <summary>
        /// Writes a warning-level entry under <paramref name="category"/>.
        /// </summary>
        public void LogWarning(string category, string message, IReadOnlyDictionary<string, object?>? attributes = null)
        {
            Write(logger, LogLevel.Warning, category, message, exception: null, attributes);
        }

        /// <summary>
        /// Writes an error-level entry under <paramref name="category"/>. Captures <paramref name="exception"/> when supplied.
        /// </summary>
        public void LogError(string category, string message, Exception? exception = null, IReadOnlyDictionary<string, object?>? attributes = null)
        {
            Write(logger, LogLevel.Error, category, message, exception, attributes);
        }

        /// <summary>
        /// Writes a critical-level entry under <paramref name="category"/>. Captures <paramref name="exception"/> when supplied.
        /// </summary>
        public void LogCritical(string category, string message, Exception? exception = null, IReadOnlyDictionary<string, object?>? attributes = null)
        {
            Write(logger, LogLevel.Critical, category, message, exception, attributes);
        }

        /// <summary>
        /// Writes an entry at the supplied <paramref name="level"/>.
        /// </summary>
        public void Log(LogLevel level, string category, string message, Exception? exception = null, IReadOnlyDictionary<string, object?>? attributes = null)
        {
            Write(logger, level, category, message, exception, attributes);
        }

        /// <summary>
        /// Opens a scope keyed on a freshly built entry. Convenience wrapper around
        /// <see cref="ILogger.BeginScope(ILoggerEntry)"/>.
        /// </summary>
        public IScopedLogger BeginScope(string category, string message, IReadOnlyDictionary<string, object?>? attributes = null)
        {
            ArgumentNullException.ThrowIfNull(logger);
            ArgumentException.ThrowIfNullOrEmpty(category);

            var seed = new LoggerEntry(
                level: LogLevel.Information,
                category: category,
                message: message,
                attributes: attributes);

            return logger.BeginScope(seed);
        }
    }


    private static void Write(
        ILogger logger,
        LogLevel level,
        string category,
        string message,
        Exception? exception,
        IReadOnlyDictionary<string, object?>? attributes)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentException.ThrowIfNullOrEmpty(category);

        if (!logger.IsEnabled(level))
        {
            return;
        }

        var entry = new LoggerEntry(
            level: level,
            category: category,
            message: message,
            exception: exception,
            attributes: attributes);

        logger.Log(entry);
    }
}
