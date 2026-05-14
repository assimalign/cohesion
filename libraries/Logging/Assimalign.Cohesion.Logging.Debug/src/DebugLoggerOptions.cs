using System;

namespace Assimalign.Cohesion.Logging;

/// <summary>
/// Configuration shape for <see cref="DebugLoggerProvider"/>.
/// </summary>
public sealed class DebugLoggerOptions
{
    /// <summary>
    /// When <see langword="true"/>, the provider emits entries only while a debugger is
    /// attached. When <see langword="false"/>, the provider always emits (useful for tests).
    /// Defaults to <see langword="true"/>.
    /// </summary>
    public bool EmitOnlyWhenDebuggerAttached { get; set; } = true;

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
    /// Optional callback that receives the rendered line. When set, the provider uses it instead
    /// of <c>System.Diagnostics.Debug.WriteLine</c>. Use this for tests, or to redirect to a
    /// captured debug listener.
    /// </summary>
    public Action<string>? Writer { get; set; }
}
