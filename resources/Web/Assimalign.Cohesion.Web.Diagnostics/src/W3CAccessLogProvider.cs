using System;
using System.IO;

namespace Assimalign.Cohesion.Web.Diagnostics;

using Assimalign.Cohesion.Logging;
using Assimalign.Cohesion.Web.Diagnostics.Internal;

/// <summary>
/// An <see cref="ILoggerProvider"/> that renders completed HTTP exchanges as W3C extended or
/// NCSA common/combined access-log files with buffered, size- and day-rolled output.
/// </summary>
/// <remarks>
/// <para>
/// The provider is a plain member of the Cohesion logging pipeline: register it on the
/// application's <see cref="ILoggerFactoryBuilder"/> next to console/debug providers and let
/// the HTTP logging middleware (<c>UseHttpLogging</c>) emit through the composed logger. It
/// renders only entries stamped <see cref="HttpLoggingAttributes.Event"/> =
/// <see cref="HttpLoggingAttributes.EventExchange"/> — one line per completed exchange — and
/// ignores everything else, so it is safe in a factory that also carries application logging.
/// Use a <see cref="LoggerFilterRule"/> scoped to this provider's type to trim the fan-out when
/// application log volume is high.
/// </para>
/// <para>
/// Files are written to <see cref="W3CAccessLogOptions.Directory"/> as
/// <c>{prefix}-{yyyyMMdd}.log</c>, rolling to <c>{prefix}-{yyyyMMdd}.{seq:000}.log</c> when
/// <see cref="W3CAccessLogOptions.FileSizeLimit"/> is exceeded, with oldest-first deletion
/// beyond <see cref="W3CAccessLogOptions.RetainedFileCountLimit"/>. Output is buffered and
/// flushed on <see cref="W3CAccessLogOptions.FlushInterval"/>, on <see cref="Flush"/>, and on
/// disposal. The provider is thread-safe; lines from concurrent exchanges never interleave.
/// </para>
/// </remarks>
public sealed class W3CAccessLogProvider : LoggerProvider
{
    private readonly W3CAccessLogWriter _writer;

    /// <summary>
    /// Initializes the provider from the supplied options. The options are validated and
    /// copied; later mutation has no effect.
    /// </summary>
    /// <param name="options">The access-log options. <see cref="W3CAccessLogOptions.Directory"/> is required.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <see cref="W3CAccessLogOptions.Directory"/> is null or empty, the file name prefix is
    /// empty or contains invalid file name characters, or the format is not a defined
    /// <see cref="AccessLogFormat"/> value.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// A size, retention, or flush-interval option is zero or negative where a positive value is
    /// required.
    /// </exception>
    public W3CAccessLogProvider(W3CAccessLogOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrEmpty(options.Directory, "options.Directory");
        ArgumentException.ThrowIfNullOrEmpty(options.FileNamePrefix, "options.FileNamePrefix");

        if (options.FileNamePrefix.AsSpan().IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException("The file name prefix contains invalid file name characters.", nameof(options));
        }

        if (options.Format is not (AccessLogFormat.W3CExtended or AccessLogFormat.Common or AccessLogFormat.Combined))
        {
            throw new ArgumentException($"Unknown access log format '{options.Format}'.", nameof(options));
        }

        if (options.FileSizeLimit is { } sizeLimit)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sizeLimit, "options.FileSizeLimit");
        }

        if (options.RetainedFileCountLimit is { } retained)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(retained, "options.RetainedFileCountLimit");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(options.FlushInterval, TimeSpan.Zero, "options.FlushInterval");

        _writer = new W3CAccessLogWriter(
            options.Directory,
            options.FileNamePrefix,
            options.Format,
            options.FileSizeLimit,
            options.RetainedFileCountLimit,
            options.FlushInterval);
    }

    /// <inheritdoc />
    public override string Name => "W3CAccessLog";

    /// <summary>
    /// Flushes buffered output to disk. Called automatically on the configured
    /// <see cref="W3CAccessLogOptions.FlushInterval"/> and on disposal; call it directly when a
    /// test or shutdown path needs the file contents immediately.
    /// </summary>
    public void Flush() => _writer.Flush();

    /// <inheritdoc />
    protected override Logger CreateCore(string category) => new W3CAccessLogLogger(category, this);

    /// <inheritdoc />
    protected override void DisposeCore() => _writer.Dispose();

    internal void Write(ILoggerEntry entry)
    {
        if (IsDisposed)
        {
            return;
        }

        // One line per completed exchange: anything not stamped as an exchange event (request
        // starts, scope seeds, application logging sharing the factory) is not an access-log
        // line and is ignored.
        if (entry.Attributes.TryGetValue(HttpLoggingAttributes.Event, out object? value)
            && value is HttpLoggingAttributes.EventExchange)
        {
            _writer.Write(entry);
        }
    }

    private sealed class W3CAccessLogLogger : Logger
    {
        private readonly W3CAccessLogProvider _provider;

        public W3CAccessLogLogger(string category, W3CAccessLogProvider provider)
            : base(category)
        {
            _provider = provider;
        }

        public override bool IsEnabled(LogLevel level) => base.IsEnabled(level) && !_provider.IsDisposed;

        protected override void WriteCore(ILoggerEntry entry) => _provider.Write(entry);

        protected override IScopedLogger BeginScopeCore(ILoggerEntry entry)
        {
            // Scope seeds are not exchanges; Write filters them out. The scope simply keeps
            // routing entries to the provider.
            _provider.Write(entry);
            return new W3CAccessLogScopedLogger(Category, _provider, entry.Id);
        }
    }

    private sealed class W3CAccessLogScopedLogger : ScopedLogger
    {
        private readonly W3CAccessLogProvider _provider;

        public W3CAccessLogScopedLogger(string category, W3CAccessLogProvider provider, LogId parentId)
            : base(category, parentId)
        {
            _provider = provider;
        }

        public override bool IsEnabled(LogLevel level) => base.IsEnabled(level) && !_provider.IsDisposed;

        protected override void WriteCore(ILoggerEntry entry) => _provider.Write(entry);

        protected override IScopedLogger BeginScopeCore(ILoggerEntry entry)
        {
            _provider.Write(entry);
            return new W3CAccessLogScopedLogger(Category, _provider, entry.Id);
        }
    }
}
