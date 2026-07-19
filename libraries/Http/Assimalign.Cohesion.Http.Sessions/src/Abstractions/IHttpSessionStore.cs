using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// The asynchronous backing-store seam for HTTP session state. A store persists
/// an opaque, length-prefixed session payload (produced by
/// <see cref="HttpSessionSerializer"/>) keyed by session id, with sliding
/// idle-timeout metadata. The in-process default is
/// <see cref="InMemoryHttpSessionStore"/>; an out-of-process store (distributed
/// cache, key/value resource, database) implements the same contract so callers
/// round-trip identical bytes without change.
/// </summary>
/// <remarks>
/// <para>
/// <b>Opaque payloads.</b> The store never inspects or interprets the payload —
/// framing is the session's concern, not the store's. Any implementation that
/// returns the exact bytes it was given round-trips a session losslessly, which
/// is what lets the same session objects run over any backend.
/// </para>
/// <para>
/// <b>Concurrency contract — last-commit-wins.</b> The store performs no
/// read-modify-write locking across a request. Two concurrent requests for the
/// same session id each read a snapshot, mutate locally, and write the whole
/// payload back; the write that completes last replaces the payload
/// <em>wholesale</em> — there is no per-key merge. Distributed implementations
/// MUST honor the same semantics: <see cref="SetAsync"/> is an unconditional
/// overwrite. Compare-and-swap / ETag concurrency is intentionally not part of
/// this contract; a backend that offers it (for example a key/value resource
/// with CAS) may expose stronger guarantees through a separate adapter surface,
/// but the session pipeline relies only on last-commit-wins.
/// </para>
/// <para>
/// <b>Sliding expiration.</b> A live entry's idle window is renewed on access:
/// <see cref="GetAsync"/> and <see cref="RefreshAsync"/> both push the entry's
/// expiry to <c>now + idleTimeout</c>. An entry whose window has elapsed is
/// treated as absent (<see cref="GetAsync"/> returns <see langword="null"/>).
/// </para>
/// <para>
/// <b>AOT posture.</b> The contract carries only <see cref="byte"/> arrays and
/// value types; no reflection or dynamic serialization is implied. Payload
/// framing rides <see cref="HttpSessionSerializer"/>, which is fully
/// AOT/trim-safe.
/// </para>
/// </remarks>
public interface IHttpSessionStore
{
    /// <summary>
    /// Reads the payload stored under <paramref name="sessionId"/>, renewing its
    /// idle window on a hit (renew-on-access sliding expiration).
    /// </summary>
    /// <param name="sessionId">The session identifier to read.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>
    /// The stored payload, or <see langword="null"/> when no live entry exists
    /// (never stored, removed, or expired).
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="sessionId"/> is <see langword="null"/> or empty.</exception>
    ValueTask<byte[]?> GetAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes (or replaces) the payload stored under <paramref name="sessionId"/>
    /// and sets its idle window to <paramref name="idleTimeout"/>. The write is an
    /// unconditional overwrite per the last-commit-wins contract.
    /// </summary>
    /// <param name="sessionId">The session identifier to write.</param>
    /// <param name="payload">The opaque payload to store.</param>
    /// <param name="idleTimeout">The idle window after which the entry expires if not accessed.</param>
    /// <param name="cancellationToken">A token to cancel the write.</param>
    /// <returns>A task that completes when the payload has been stored.</returns>
    /// <exception cref="ArgumentException"><paramref name="sessionId"/> is <see langword="null"/> or empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="payload"/> is <see langword="null"/>.</exception>
    ValueTask SetAsync(string sessionId, byte[] payload, TimeSpan idleTimeout, CancellationToken cancellationToken = default);

    /// <summary>
    /// Renews the idle window of the entry stored under <paramref name="sessionId"/>
    /// to <paramref name="idleTimeout"/> without rewriting its payload. A no-op when
    /// no live entry exists.
    /// </summary>
    /// <param name="sessionId">The session identifier to renew.</param>
    /// <param name="idleTimeout">The idle window to slide the entry to.</param>
    /// <param name="cancellationToken">A token to cancel the renewal.</param>
    /// <returns>A task that completes when the entry has been renewed (or found absent).</returns>
    /// <exception cref="ArgumentException"><paramref name="sessionId"/> is <see langword="null"/> or empty.</exception>
    ValueTask RefreshAsync(string sessionId, TimeSpan idleTimeout, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the entry stored under <paramref name="sessionId"/>. A no-op when no
    /// entry exists. Used both for explicit session abandonment and for the
    /// old-id cleanup step of session-id regeneration.
    /// </summary>
    /// <param name="sessionId">The session identifier to remove.</param>
    /// <param name="cancellationToken">A token to cancel the removal.</param>
    /// <returns>A task that completes when the entry has been removed (or found absent).</returns>
    /// <exception cref="ArgumentException"><paramref name="sessionId"/> is <see langword="null"/> or empty.</exception>
    ValueTask RemoveAsync(string sessionId, CancellationToken cancellationToken = default);
}
