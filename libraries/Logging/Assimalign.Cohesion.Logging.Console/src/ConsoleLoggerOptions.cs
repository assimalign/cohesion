using System;
using System.IO;

namespace Assimalign.Cohesion.Logging.Console;

/// <summary>
/// Configuration shape for <see cref="ConsoleLoggerProvider"/>.
/// </summary>
public sealed class ConsoleLoggerOptions
{
    /// <summary>
    /// Output stream for non-error entries. Defaults to <see cref="System.Console.Out"/> at
    /// resolution time when null.
    /// </summary>
    public TextWriter? Output { get; set; }

    /// <summary>
    /// Output stream for entries at <see cref="LogLevel.Error"/> or above. Defaults to
    /// <see cref="System.Console.Error"/> when null.
    /// </summary>
    public TextWriter? ErrorOutput { get; set; }

    /// <summary>
    /// When <see langword="true"/>, the provider includes the entry's structured attributes in
    /// the rendered output. Defaults to <see langword="true"/>.
    /// </summary>
    public bool IncludeAttributes { get; set; } = true;

    /// <summary>
    /// When <see langword="true"/>, the provider includes the entry's exception (if any) in the
    /// rendered output. Defaults to <see langword="true"/>.
    /// </summary>
    public bool IncludeException { get; set; } = true;

    /// <summary>
    /// When <see langword="true"/>, the provider includes the parent log id in the rendered
    /// output. Defaults to <see langword="false"/>.
    /// </summary>
    public bool IncludeParentId { get; set; }

    /// <summary>
    /// Optional formatter override. When non-null, the provider invokes it instead of the
    /// built-in renderer.
    /// </summary>
    public Action<ILogEntry, TextWriter>? Formatter { get; set; }
}
