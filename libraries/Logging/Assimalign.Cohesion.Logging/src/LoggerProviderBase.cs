using System;
using System.Threading;

namespace Assimalign.Cohesion.Logging;

/// <summary>
/// Reusable base class for <see cref="ILoggerProvider"/> implementations.
/// </summary>
/// <remarks>
/// <para>
/// Implements the boilerplate (category validation, idempotent disposal, the disposed flag) so
/// concrete providers only implement <see cref="CreateCore"/> and optionally
/// <see cref="DisposeCore"/>. <see cref="Create(string)"/> returns the concrete
/// <see cref="LoggerBase"/> rather than the <see cref="ILogger"/> interface so callers that
/// hold a strongly typed reference pay only one virtual dispatch per log call instead of two.
/// </para>
/// </remarks>
public abstract class LoggerProviderBase : ILoggerProvider
{
    private int _disposed;

    /// <inheritdoc />
    public abstract string Name { get; }

    /// <summary>True when <see cref="Dispose"/> has run.</summary>
    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>
    /// Returns a new logger for <paramref name="category"/>. Covariant return type so callers
    /// that hold a typed <see cref="LoggerProviderBase"/> reference get the concrete logger
    /// without an interface cast.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="category"/> is null or empty.</exception>
    /// <exception cref="ObjectDisposedException">The provider has been disposed.</exception>
    public LoggerBase Create(string category)
    {
        ArgumentException.ThrowIfNullOrEmpty(category);

        if (IsDisposed)
        {
            throw new ObjectDisposedException(GetType().Name);
        }

        return CreateCore(category);
    }

    ILogger ILoggerProvider.Create(string category) => Create(category);

    /// <summary>
    /// Implementation hook: build the concrete logger for the supplied category.
    /// </summary>
    protected abstract LoggerBase CreateCore(string category);

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
    /// Implementation hook: release provider-owned resources. Defaults to a no-op.
    /// </summary>
    protected virtual void DisposeCore()
    {
    }
}
