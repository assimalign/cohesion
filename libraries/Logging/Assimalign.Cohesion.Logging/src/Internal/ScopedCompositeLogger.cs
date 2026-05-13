using System;
using System.Threading;

namespace Assimalign.Cohesion.Logging.Internal;

/// <summary>
/// Scope handle returned from <see cref="CompositeLogger.BeginScope(ILogEntry)"/>. Re-emits the
/// composite's behavior but stamps the scope's parent id onto every entry written through it.
/// </summary>
internal sealed class ScopedCompositeLogger : IScopedLogger
{
    private readonly CompositeLogger _composite;
    private readonly IScopedLogger[] _underlyingScopes;
    private int _disposed;

    public ScopedCompositeLogger(CompositeLogger composite, LogId parentId, IScopedLogger[] underlyingScopes)
    {
        _composite = composite;
        ParentId = parentId;
        _underlyingScopes = underlyingScopes;
    }

    public LogId ParentId { get; }

    public bool IsEnabled(LogLevel level) => _composite.IsEnabled(level);

    public void Log(ILogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ThrowIfDisposed();

        // Stamp the scope's parent id onto entries that did not already carry one. We never
        // overwrite an explicit parent id - that supports nested scopes where the caller already
        // built their own chain.
        var stamped = entry.ParentId is null
            ? new LogEntry(
                level: entry.Level,
                category: entry.Category,
                message: entry.Message,
                exception: entry.Exception,
                attributes: entry.Attributes,
                parentId: ParentId,
                id: entry.Id,
                timestamp: entry.Timestamp)
            : entry;

        _composite.Log(stamped);
    }

    public IScopedLogger BeginScope(ILogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ThrowIfDisposed();

        // Stamp the parent id on the seed before opening a nested scope so children of this scope
        // chain back to this scope's seed.
        var stamped = entry.ParentId is null
            ? new LogEntry(
                level: entry.Level,
                category: entry.Category,
                message: entry.Message,
                exception: entry.Exception,
                attributes: entry.Attributes,
                parentId: ParentId,
                id: entry.Id,
                timestamp: entry.Timestamp)
            : entry;

        return _composite.BeginScope(stamped);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        for (int i = 0; i < _underlyingScopes.Length; i++)
        {
            try
            {
                _underlyingScopes[i].Dispose();
            }
            catch
            {
                // Underlying scope disposal must not abort the rest of teardown.
            }
        }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(ScopedCompositeLogger));
        }
    }
}
