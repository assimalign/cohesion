using System;
using System.Linq;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Type-based access helpers for <see cref="IHttpFeatureCollection"/>. The core
/// contract is name-keyed (<see cref="IHttpFeatureCollection.Get(string)"/>),
/// but most call sites know the feature interface they want rather than its
/// installed name; these extensions resolve the interface to a registered
/// implementation by enumerating the collection.
/// </summary>
/// <remarks>
/// <para>
/// Lookups are <c>O(n)</c> over the registered features, where <c>n</c> is
/// typically &lt; 10 per exchange. The cost is negligible compared with the
/// per-exchange wire work the surrounding pipeline does, and the ergonomic
/// win (no magic-string keys at call sites) is worth it for the common path.
/// Hot-path consumers that need <c>O(1)</c> access can cache the resolved
/// feature reference at first lookup.
/// </para>
/// </remarks>
public static class HttpFeatureCollectionExtensions
{
    extension(IHttpFeatureCollection features)
    {
        /// <summary>
        /// Returns the first installed feature that implements
        /// <typeparamref name="TFeature"/>, or <see langword="null"/> when none
        /// is registered.
        /// </summary>
        /// <typeparam name="TFeature">The feature contract to look up.</typeparam>
        /// <returns>The resolved feature, or <see langword="null"/>.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="features"/> is <see langword="null"/>.</exception>
        public TFeature? Get<TFeature>() where TFeature : class, IHttpFeature
        {
            ArgumentNullException.ThrowIfNull(features);
            return features.OfType<TFeature>().FirstOrDefault();
        }


        /// <summary>
        /// Registers <paramref name="feature"/> using its <see cref="IHttpFeature.Name"/>
        /// as the slot. Pass <see langword="null"/> to remove the currently-installed
        /// feature that implements <typeparamref name="TFeature"/>; this preserves
        /// the semantic of the pre-IHttpFeature <c>Set&lt;T&gt;(null)</c> shape so
        /// callers don't need to know the impl's name to remove it.
        /// </summary>
        /// <typeparam name="TFeature">The feature contract being registered.</typeparam>
        /// <param name="features">The feature collection.</param>
        /// <param name="feature">The implementation, or <see langword="null"/> to remove.</param>
        /// <exception cref="ArgumentNullException"><paramref name="features"/> is <see langword="null"/>.</exception>
        public void Set<TFeature>(TFeature? feature) where TFeature : class, IHttpFeature
        {
            ArgumentNullException.ThrowIfNull(features);

            if (feature is null)
            {
                TFeature? existing = features.OfType<TFeature>().FirstOrDefault();
                if (existing is not null)
                {
                    features.Remove(existing.Name);
                }
                return;
            }

            features.Set(feature);
        }
    }
}
