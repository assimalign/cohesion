using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Connections;

/// <summary>
/// Provides named optional connection features, including features exposed by a wrapped connection.
/// </summary>
public interface IConnectionFeatureCollection : IEnumerable<IConnectionFeature>
{
    /// <summary>
    /// Gets the feature registered under the exact, case-sensitive <paramref name="name"/>.
    /// </summary>
    /// <param name="name">The feature name.</param>
    /// <returns>The effective feature, or <see langword="null"/> when absent or hidden.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is null or empty.</exception>
    IConnectionFeature? Get(string name);

    /// <summary>
    /// Registers a feature, replacing any local or wrapped feature with the same name.
    /// </summary>
    /// <typeparam name="TFeature">The feature contract or implementation.</typeparam>
    /// <param name="feature">The feature to register.</param>
    /// <exception cref="ArgumentNullException"><paramref name="feature"/> is null.</exception>
    /// <exception cref="ArgumentException">The feature's name is null or empty.</exception>
    void Set<TFeature>(TFeature feature) where TFeature : class, IConnectionFeature;

    /// <summary>
    /// Removes a named feature and hides that name in the wrapped collection, including future registrations.
    /// </summary>
    /// <param name="name">The feature name to hide.</param>
    /// <returns><see langword="true"/> when a visible feature was removed; otherwise, <see langword="false"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is null or empty.</exception>
    bool Remove(string name);
}
