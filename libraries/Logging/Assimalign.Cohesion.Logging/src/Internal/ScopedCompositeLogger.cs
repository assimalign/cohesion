using System;

namespace Assimalign.Cohesion.Logging.Internal;

/// <summary>
/// Scope handle returned from <see cref="CompositeLogger.BeginScope(ILoggerEntry)"/>. Re-emits
/// the composite's behavior but stamps the scope's parent id onto every entry written through
/// it.
/// </summary>
internal sealed class ScopedCompositeLogger : ScopedLoggerBase
{
    private readonly CompositeLogger _composite;
    private readonly IScopedLogger[] _underlyingScopes;

    public ScopedCompositeLogger(
        CompositeLogger composite,
        string category,
        LogId parentId,
        IScopedLogger[] underlyingScopes)
        : base(category, parentId)
    {
        _composite = composite;
        _underlyingScopes = underlyingScopes;
    }

    public override bool IsEnabled(LogLevel level) => !IsDisposed && _composite.IsEnabled(level);

    protected override void WriteCore(ILoggerEntry entry)
    {
        if (IsDisposed)
        {
            throw new ObjectDisposedException(nameof(ScopedCompositeLogger));
        }

        // Stamp the scope's parent id onto entries that did not already carry one. We never
        // overwrite an explicit parent id - that supports nested scopes where the caller already
        // built their own chain.
        var stamped = entry.ParentId is null
            ? new LoggerEntry(
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

    protected override IScopedLogger BeginScopeCore(ILoggerEntry entry)
    {
        if (IsDisposed)
        {
            throw new ObjectDisposedException(nameof(ScopedCompositeLogger));
        }

        // Stamp the parent id on the seed before opening a nested scope so children of this
        // scope chain back to this scope's seed.
        var stamped = entry.ParentId is null
            ? new LoggerEntry(
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

    protected override void DisposeCore()
    {
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
}
