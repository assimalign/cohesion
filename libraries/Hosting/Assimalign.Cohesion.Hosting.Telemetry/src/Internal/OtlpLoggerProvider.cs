using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Logging;
using Assimalign.Cohesion.OpenTelemetry;

namespace Assimalign.Cohesion.Hosting.Telemetry.Internal;

internal sealed class OtlpLoggerProvider : LoggerProvider
{
    /// <summary>Initializes a new instance of the <see cref="OtlpLoggerProvider"/> class.</summary>
    /// <param name="exporter">The OTLP log exporter that receives the provider's log records.</param>
    public OtlpLoggerProvider(IOtlpLogExporter exporter)
    {
        Exporter = exporter;
    }

    internal IOtlpLogExporter Exporter { get; }
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

    private sealed class OtlpLogger : Logger
    {
        private readonly OtlpLoggerProvider _provider;

        /// <summary>Initializes a new instance of the <see cref="OtlpLogger"/> class.</summary>
        /// <param name="category">The logger category.</param>
        /// <param name="provider">The provider that exports the logger's entries.</param>
        public OtlpLogger(string category, OtlpLoggerProvider provider)
            : base(category)
        {
            _provider = provider;
        }

        protected override void WriteCore(ILoggerEntry entry) => _provider.Write(entry);
        protected override IScopedLogger BeginScopeCore(ILoggerEntry entry)
        {
            _provider.Write(entry);
            return new OtlpScope(Category, _provider, entry.Id);
        }
    }

    private sealed class OtlpScope : ScopedLogger
    {
        private readonly OtlpLoggerProvider _provider;

        /// <summary>Initializes a new instance of the <see cref="OtlpScope"/> class.</summary>
        /// <param name="category">The logger category.</param>
        /// <param name="provider">The provider that exports the scope's entries.</param>
        /// <param name="parent">The identifier of the entry that opened the scope.</param>
        public OtlpScope(string category, OtlpLoggerProvider provider, LogId parent)
            : base(category, parent)
        {
            _provider = provider;
        }

        protected override void WriteCore(ILoggerEntry entry) => _provider.Write(entry);
        protected override IScopedLogger BeginScopeCore(ILoggerEntry entry)
        {
            _provider.Write(entry);
            return new OtlpScope(Category, _provider, entry.Id);
        }
    }
}
