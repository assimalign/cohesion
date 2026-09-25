using System;

using Assimalign.Cohesion.Http.Internal;

// Deviates from the repo namespace-matches-assembly rule per design decision:
// session extensions retain the established HTTP family namespace.
namespace Assimalign.Cohesion.Http;

/// <summary>
/// Creates buffered HTTP sessions over an opaque session payload store.
/// </summary>
public static class HttpSessionStoreExtensions
{
    /// <summary>
    /// Provides session creation for a backing store.
    /// </summary>
    /// <param name="store">The backing store used for session load and commit operations.</param>
    extension(IHttpSessionStore store)
    {
        /// <summary>
        /// Creates an unloaded session whose state round-trips through the store.
        /// </summary>
        /// <param name="id">The nonempty session identifier.</param>
        /// <param name="idleTimeout">The positive idle window used when committing or refreshing state.</param>
        /// <returns>A new session with an empty buffer; creation performs no store I/O.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="store"/> or <paramref name="id"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException"><paramref name="id"/> is empty.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="idleTimeout"/> is not positive.</exception>
        public IHttpStoredSession CreateSession(string id, TimeSpan idleTimeout)
        {
            ArgumentNullException.ThrowIfNull(store);
            ArgumentException.ThrowIfNullOrEmpty(id);
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(idleTimeout, TimeSpan.Zero);

            return new HttpSessionStoreSession(id, store, idleTimeout);
        }
    }
}
