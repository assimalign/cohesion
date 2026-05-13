using System;
using System.Globalization;
using System.IO;

namespace Assimalign.Cohesion.Logging.Console.Internal;

/// <summary>
/// Default text renderer for console log entries. Output format:
/// <c>[timestamp] [LEVEL] category: message {attribute=value, ...} [parentId=...] [exception]</c>.
/// </summary>
internal static class ConsoleLogFormatter
{
    public static void Write(ILogEntry entry, TextWriter writer, ConsoleLoggerOptions options)
    {
        writer.Write('[');
        writer.Write(entry.Timestamp.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        writer.Write("] [");
        writer.Write(LevelToString(entry.Level));
        writer.Write("] ");
        writer.Write(entry.Category);
        writer.Write(": ");
        writer.Write(entry.Message);

        if (options.IncludeAttributes && entry.Attributes.Count > 0)
        {
            writer.Write(" {");
            bool first = true;
            foreach (var pair in entry.Attributes)
            {
                if (!first)
                {
                    writer.Write(", ");
                }
                writer.Write(pair.Key);
                writer.Write('=');
                writer.Write(pair.Value is null ? "null" : pair.Value.ToString());
                first = false;
            }
            writer.Write('}');
        }

        if (options.IncludeParentId && entry.ParentId is { } parentId)
        {
            writer.Write(" [parentId=");
            writer.Write(parentId.ToString());
            writer.Write(']');
        }

        writer.WriteLine();

        if (options.IncludeException && entry.Exception is { } exception)
        {
            writer.WriteLine(exception);
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
