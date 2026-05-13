using System;

namespace Assimalign.Cohesion.Logging.Internal;

/// <summary>
/// Substitute scoped logger used when an underlying provider throws while opening a scope. Keeps
/// the parent <see cref="CompositeLogger"/> stable in the face of misbehaving providers.
/// </summary>
internal sealed class NoopScopedLogger : IScopedLogger
{
    public static readonly NoopScopedLogger Instance = new();

    private NoopScopedLogger()
    {
    }

    public LogId ParentId => LogId.Empty;

    public bool IsEnabled(LogLevel level) => false;

    public void Log(ILogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
    }

    public IScopedLogger BeginScope(ILogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return this;
    }

    public void Dispose()
    {
    }
}
