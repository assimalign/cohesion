using System;
using System.Threading;

namespace Assimalign.Cohesion.Logging;

/// <summary>
/// Reusable base class for <see cref="IScopedLogger"/> implementations.
/// </summary>
/// <remarks>
/// <para>
/// Inherits the template-method pattern from <see cref="Logger"/>; adds idempotent
/// disposal and a <see cref="ParentId"/> field. Derived classes only implement
/// <see cref="Logger.WriteCore"/>, <see cref="Logger.BeginScopeCore"/>, and (if needed)
/// <see cref="DisposeCore"/>.
/// </para>
/// </remarks>
public abstract class ScopedLogger : Logger, IScopedLogger
{
    private int _disposed;

    /// <summary>
    /// Initializes a scoped logger.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="category"/> is null or empty.</exception>
    protected ScopedLogger(string category, LogId parentId)
        : base(category)
    {
        ParentId = parentId;
    }

    /// <inheritdoc />
    public LogId ParentId { get; }

    /// <summary>True when <see cref="Dispose"/> has run.</summary>
    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <inheritdoc />
    public override bool IsEnabled(LogLevel level) => !IsDisposed && base.IsEnabled(level);

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        DisposeCore();
    }

    /// <summary>
    /// Implementation hook for derived classes to release scope-specific resources. Defaults to
    /// a no-op.
    /// </summary>
    protected virtual void DisposeCore()
    {
    }
}
