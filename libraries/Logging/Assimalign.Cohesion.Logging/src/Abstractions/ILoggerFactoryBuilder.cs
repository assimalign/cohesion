using System;

namespace Assimalign.Cohesion.Logging;

/// <summary>
/// Fluent registration surface for <see cref="ILoggerFactory"/>.
/// </summary>
/// <remarks>
/// Build the factory in three phases: register providers, declare the minimum level and filter
/// rules, register enrichers, then call <see cref="Build"/>. Builders are not thread-safe; treat
/// them as scratch space scoped to a single setup routine.
/// </remarks>
public interface ILoggerFactoryBuilder
{
    /// <summary>
    /// Registers a provider. Providers with duplicate <see cref="ILoggerProvider.Name"/>s are
    /// rejected so the resulting factory does not fan out to the same sink twice.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="provider"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">A provider with the same <see cref="ILoggerProvider.Name"/> is already registered.</exception>
    ILoggerFactoryBuilder AddProvider(ILoggerProvider provider);

    /// <summary>
    /// Sets the factory-wide minimum log level. Used as the fallback when no
    /// <see cref="LoggerFilterRule"/> matches a (provider, category) pair. Defaults to
    /// <see cref="LogLevel.Information"/>.
    /// </summary>
    ILoggerFactoryBuilder SetMinimumLevel(LogLevel level);

    /// <summary>
    /// Adds a filter rule. Rules are evaluated per (provider, category) pair via the algorithm
    /// documented on <see cref="LoggerFilterRule"/>.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="rule"/> is <see langword="null"/>.</exception>
    ILoggerFactoryBuilder AddRule(LoggerFilterRule rule);

    /// <summary>
    /// Convenience: adds a rule that requires the given minimum level for entries whose
    /// category begins with <paramref name="categoryPrefix"/> (case-insensitive).
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="categoryPrefix"/> is null or empty.</exception>
    ILoggerFactoryBuilder AddRule(string categoryPrefix, LogLevel minimumLevel);

    /// <summary>
    /// Registers an enricher. Enrichers run in registration order for every entry before fan-out.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="enricher"/> is <see langword="null"/>.</exception>
    ILoggerFactoryBuilder AddEnricher(ILoggerEnricher enricher);

    /// <summary>
    /// Finalizes the registration phase and returns a configured factory. After this returns the
    /// builder is no longer usable.
    /// </summary>
    /// <exception cref="InvalidOperationException">The builder has already been used to build a factory.</exception>
    ILoggerFactory Build();
}
