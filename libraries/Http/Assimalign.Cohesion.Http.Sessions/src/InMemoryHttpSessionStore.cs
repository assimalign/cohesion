using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// The default, process-local <see cref="IHttpSessionStore"/>: a concurrent
/// dictionary of session payloads with lazy, access-driven idle expiration. This
/// is the in-memory implementation moved behind the store seam — the same store
/// contract a distributed backend implements, so the session pipeline is
/// identical whether state lives in this process or in an out-of-process backend.
/// </summary>
/// <remarks>
/// <para>
/// <b>Sliding expiration is lazy.</b> There is no background eviction timer;
/// an expired entry is dropped the next time it is read or refreshed. This keeps
/// the store allocation- and thread-light and free of timer lifetime concerns —
/// active reaping of never-touched-again sessions is a non-goal for the
/// in-memory default (a distributed backend delegates expiry to its own TTL).
/// </para>
/// <para>
/// <b>Concurrency is last-commit-wins.</b> <see cref="SetAsync"/> replaces the
/// whole entry unconditionally; there is no read-modify-write lock. Renewals use
/// an atomic compare-and-set and simply skip when another writer raced ahead —
/// sliding is best-effort, correctness is not affected.
/// </para>
/// <para>
/// The clock is a <see cref="TimeProvider"/> so tests can drive expiration
/// deterministically; production uses <see cref="TimeProvider.System"/>.
/// </para>
/// </remarks>
public sealed class InMemoryHttpSessionStore : IHttpSessionStore
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Initializes a store using the system clock.
    /// </summary>
    public InMemoryHttpSessionStore()
        : this(TimeProvider.System)
    {
    }

    /// <summary>
    /// Initializes a store using the supplied clock.
    /// </summary>
    /// <param name="timeProvider">The clock used to evaluate and slide idle windows.</param>
    /// <exception cref="ArgumentNullException"><paramref name="timeProvider"/> is <see langword="null"/>.</exception>
    public InMemoryHttpSessionStore(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    public ValueTask<byte[]?> GetAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrEmpty(sessionId);

        if (!_entries.TryGetValue(sessionId, out Entry? entry))
        {
            return ValueTask.FromResult<byte[]?>(null);
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        if (entry.ExpiresAt <= now)
        {
            // Drop only this exact snapshot; a concurrent re-write must not be evicted.
            _entries.TryRemove(new KeyValuePair<string, Entry>(sessionId, entry));
            return ValueTask.FromResult<byte[]?>(null);
        }

        _entries.TryUpdate(sessionId, entry.RenewedTo(now + entry.IdleTimeout), entry);
        return ValueTask.FromResult<byte[]?>(entry.Payload);
    }

    /// <inheritdoc />
    public ValueTask SetAsync(string sessionId, byte[] payload, TimeSpan idleTimeout, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        ArgumentNullException.ThrowIfNull(payload);

        DateTimeOffset expiresAt = _timeProvider.GetUtcNow() + idleTimeout;
        _entries[sessionId] = new Entry(payload, expiresAt, idleTimeout);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask RefreshAsync(string sessionId, TimeSpan idleTimeout, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrEmpty(sessionId);

        if (!_entries.TryGetValue(sessionId, out Entry? entry))
        {
            return ValueTask.CompletedTask;
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        if (entry.ExpiresAt <= now)
        {
            _entries.TryRemove(new KeyValuePair<string, Entry>(sessionId, entry));
            return ValueTask.CompletedTask;
        }

        _entries.TryUpdate(sessionId, entry.RenewedTo(now + idleTimeout), entry);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask RemoveAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrEmpty(sessionId);

        _entries.TryRemove(sessionId, out _);
        return ValueTask.CompletedTask;
    }

    private sealed class Entry
    {
        public Entry(byte[] payload, DateTimeOffset expiresAt, TimeSpan idleTimeout)
        {
            Payload = payload;
            ExpiresAt = expiresAt;
            IdleTimeout = idleTimeout;
        }

        public byte[] Payload { get; }

        public DateTimeOffset ExpiresAt { get; }

        public TimeSpan IdleTimeout { get; }

        public Entry RenewedTo(DateTimeOffset expiresAt) => new(Payload, expiresAt, IdleTimeout);
    }
}
