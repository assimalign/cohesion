using System;

namespace Assimalign.Cohesion.Logging;

/// <summary>
/// Reusable base class for <see cref="ILogger"/> implementations.
/// </summary>
/// <remarks>
/// <para>
/// The base class folds the boilerplate of every Cohesion logger into non-virtual methods so
/// the hot paths (<see cref="Log"/> and <see cref="BeginScope"/>) make a single virtual call to
/// <see cref="WriteCore"/> or <see cref="BeginScopeCore"/> instead of dispatching through the
/// <see cref="ILogger"/> interface for every step. Derived classes only implement the actual
/// write and scope-creation work; null guards and the <see cref="IsEnabled"/> short-circuit are
/// handled here.
/// </para>
/// <para>
/// Seed-entry emission on <see cref="BeginScope"/> is the derived class's responsibility - the
/// base does not auto-emit so a fan-out logger (composite) can keep seed emission to its
/// underlying providers without double-writing.
/// </para>
/// <para>
/// Concrete implementations should be sealed to give the JIT the best chance of devirtualizing
/// calls when callers hold a strongly typed reference.
/// </para>
/// </remarks>
public abstract class Logger : ILogger
{
    /// <summary>
    /// Initializes the base with the supplied category.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="category"/> is null or empty.</exception>
    protected Logger(string category)
    {
        ArgumentException.ThrowIfNullOrEmpty(category);
        Category = category;
    }

    /// <summary>The category this logger was created for.</summary>
    public string Category { get; }

    /// <summary>
    /// Returns <see langword="true"/> when entries at <paramref name="level"/> should be
    /// considered for write. Defaults to "any level except <see cref="LogLevel.None"/>"; derived
    /// classes commonly override to factor in provider disposal or external state.
    /// </summary>
    public virtual bool IsEnabled(LogLevel level) => level != LogLevel.None;

    /// <inheritdoc />
    public void Log(ILoggerEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (!IsEnabled(entry.Level))
        {
            return;
        }

        WriteCore(entry);
    }

    /// <inheritdoc />
    public IScopedLogger BeginScope(ILoggerEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return BeginScopeCore(entry);
    }

    /// <summary>
    /// Writes <paramref name="entry"/> to the underlying sink. Called by <see cref="Log"/> after
    /// null and level checks pass.
    /// </summary>
    protected abstract void WriteCore(ILoggerEntry entry);

    /// <summary>
    /// Opens a new scope for <paramref name="entry"/>. Called by <see cref="BeginScope"/> after
    /// the null guard. Derived classes that want the seed entry to appear in their sink output
    /// MUST call <see cref="WriteCore"/> explicitly before constructing the scope.
    /// </summary>
    protected abstract IScopedLogger BeginScopeCore(ILoggerEntry entry);
}
