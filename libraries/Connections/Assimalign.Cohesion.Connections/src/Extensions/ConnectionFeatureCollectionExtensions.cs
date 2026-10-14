using System;

namespace Assimalign.Cohesion.Connections;

/// <summary>
/// Provides typed access to the effective features on a connection.
/// </summary>
public static partial class ConnectionFeatureCollectionExtensions
{
    extension(IConnectionFeatureCollection features)
    {
        /// <summary>
        /// Gets the first feature implementing <typeparamref name="TFeature"/>, visiting the
        /// connection's own features before visible features from wrapped connections.
        /// </summary>
        /// <typeparam name="TFeature">The feature contract or implementation to find.</typeparam>
        /// <returns>The matching feature, or <see langword="null"/> when absent.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="features"/> is null.</exception>
        /// <remarks>
        /// Lookups through <see cref="ConnectionFeatureCollection"/> layers allocate nothing.
        /// Other implementations use their own enumerator. Cache a resolved feature for repeated I/O.
        /// </remarks>
        public TFeature? Get<TFeature>() where TFeature : class, IConnectionFeature
        {
            ArgumentNullException.ThrowIfNull(features);
            if (features.GetType() == typeof(ConnectionFeatureCollection))
            {
                return ((ConnectionFeatureCollection)features).GetFeature<TFeature>();
            }

            foreach (IConnectionFeature feature in features)
            {
                if (feature is TFeature match)
                {
                    return match;
                }
            }

            return null;
        }
    }
}
