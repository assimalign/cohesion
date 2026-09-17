using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Logging;
using Assimalign.Cohesion.OpenTelemetry;

namespace Assimalign.Cohesion.Hosting.Telemetry;

internal sealed class OtlpLoggerProvider(IOtlpLogExporter exporter) : LoggerProvider
{
    internal IOtlpLogExporter Exporter { get; } = exporter;
    public override string Name => "OpenTelemetry";
    protected override Logger CreateCore(string category) => new OtlpLogger(category, this);
    protected override void DisposeCore() => Exporter.DisposeAsync().AsTask().GetAwaiter().GetResult();

    internal void Write(ILoggerEntry entry)
    {
        if (IsDisposed || entry.Level == LogLevel.None)
        {
            return;
        }

        try
        {
            (int severity, string text) = Severity(entry.Level);
            var attributes = new Dictionary<string, object?>(entry.Attributes, StringComparer.Ordinal)
            {
                ["cohesion.log.id"] = entry.Id.ToString()
            };
            if (entry.ParentId is { } parent)
            {
                attributes["cohesion.log.parent_id"] = parent.ToString();
            }

            if (entry.Exception is { } exception)
            {
                attributes["exception.type"] = exception.GetType().FullName;
                attributes["exception.message"] = exception.Message;
                attributes["exception.stacktrace"] = exception.ToString();
            }
            Exporter.TryEnqueue(new OtlpLogRecord(entry.Timestamp, severity, text, entry.Message, entry.Category, attributes, null, null));
        }
        // Logging is an isolation boundary, including custom ILoggerEntry implementations.
        catch (Exception) { }
    }

    internal static (int Number, string Text) Severity(LogLevel level) => level switch
    {
        LogLevel.Trace => (1, "TRACE"), LogLevel.Debug => (5, "DEBUG"),
        LogLevel.Information => (9, "INFO"), LogLevel.Warning => (13, "WARN"),
        LogLevel.Error => (17, "ERROR"), LogLevel.Critical => (21, "FATAL"),
        LogLevel.Event => (10, "EVENT"), LogLevel.None => throw new ArgumentOutOfRangeException(nameof(level), "None is not exported."),
        _ => throw new ArgumentOutOfRangeException(nameof(level), "Unknown log severity.")
    };

    private sealed class OtlpLogger(string category, OtlpLoggerProvider provider) : Logger(category)
    {
        protected override void WriteCore(ILoggerEntry entry) => provider.Write(entry);
        protected override IScopedLogger BeginScopeCore(ILoggerEntry entry)
        {
            provider.Write(entry);
            return new OtlpScope(Category, provider, entry.Id);
        }
    }

    private sealed class OtlpScope(string category, OtlpLoggerProvider provider, LogId parent) : ScopedLogger(category, parent)
    {
        protected override void WriteCore(ILoggerEntry entry) => provider.Write(entry);
        protected override IScopedLogger BeginScopeCore(ILoggerEntry entry)
        {
            provider.Write(entry);
            return new OtlpScope(Category, provider, entry.Id);
        }
    }
}
