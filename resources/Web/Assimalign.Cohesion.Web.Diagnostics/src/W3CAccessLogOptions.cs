using System;

namespace Assimalign.Cohesion.Web.Diagnostics;

/// <summary>
/// Configuration shape for <see cref="W3CAccessLogProvider"/>.
/// </summary>
/// <remarks>
/// The options are validated and copied when the provider is constructed; later mutation has no
/// effect.
/// </remarks>
public sealed class W3CAccessLogOptions
{
    /// <summary>
    /// Gets or sets the directory the log files are written to. Required; created on first
    /// write when it does not exist.
    /// </summary>
    public string? Directory { get; set; }

    /// <summary>
    /// Gets or sets the log file name prefix. Files are named
    /// <c>{prefix}-{yyyyMMdd}.log</c>, with a <c>.{seq:000}</c> segment appended when
    /// <see cref="FileSizeLimit"/> rolls a day over multiple files. Defaults to <c>access</c>.
    /// </summary>
    public string FileNamePrefix { get; set; } = "access";

    /// <summary>
    /// Gets or sets the line format. Defaults to <see cref="AccessLogFormat.W3CExtended"/>.
    /// </summary>
    public AccessLogFormat Format { get; set; } = AccessLogFormat.W3CExtended;

    /// <summary>
    /// Gets or sets the approximate per-file size limit in bytes; when a file exceeds it, the
    /// provider rolls to the next sequence number for the same UTC day. <see langword="null"/>
    /// disables size-based rolling. Defaults to 10 MiB.
    /// </summary>
    public long? FileSizeLimit { get; set; } = 10 * 1024 * 1024;

    /// <summary>
    /// Gets or sets how many log files are retained; when a roll creates a new file, the oldest
    /// files (ordinal name order) beyond the limit are deleted. <see langword="null"/> disables
    /// retention. Defaults to 31.
    /// </summary>
    public int? RetainedFileCountLimit { get; set; } = 31;

    /// <summary>
    /// Gets or sets how often buffered output is flushed to disk. Writes are buffered between
    /// flushes; <see cref="TimeSpan.Zero"/> flushes after every entry. Buffered output is also
    /// flushed when the provider is disposed or <see cref="W3CAccessLogProvider.Flush"/> is
    /// called. Defaults to one second.
    /// </summary>
    public TimeSpan FlushInterval { get; set; } = TimeSpan.FromSeconds(1);
}
