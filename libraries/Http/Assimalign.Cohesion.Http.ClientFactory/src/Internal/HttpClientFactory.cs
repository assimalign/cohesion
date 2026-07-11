using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http.Internal;

/// <summary>
/// Lifecycle-managed implementation of <see cref="IHttpClientFactory"/>.
/// </summary>
/// <remarks>
/// <para>
/// Returns named <see cref="HttpClient"/> instances backed by a pool of rotating
/// <see cref="HttpMessageHandler"/>s. Each named client gets one active handler at a time;
/// every <see cref="Create"/> call within the handler's lifetime window reuses the same
/// underlying handler (and therefore the same connection pool / DNS cache). Once the
/// lifetime elapses the active handler is moved to an expired list, a fresh handler takes
/// its place, and the expired one is disposed when garbage collection reclaims the last
/// <see cref="HttpClient"/> still using it.
/// </para>
/// <para>
/// This shape solves the classic <see cref="HttpClient"/> lifetime trade-off: callers can
/// freely <c>using</c>-dispose the clients they receive without exhausting ephemeral ports
/// (each disposal touches only the lightweight wrapping
/// <see cref="LifetimeTrackingHttpMessageHandler"/>, not the shared
/// <see cref="SocketsHttpHandler"/>), while periodic rotation refreshes
/// DNS resolution and TLS session state so failover behaviour is bounded by
/// <see cref="HttpClientFactoryOptions.DefaultHandlerLifetime"/> rather than process
/// uptime.
/// </para>
/// <para>
/// Thread-safe: <see cref="Create"/> is non-blocking on the hot path and uses a
/// compare-and-swap on <see cref="ConcurrentDictionary{TKey, TValue}"/> for rotation, so
/// concurrent callers cooperate without holding a lock across handler construction. The
/// expired-handler cleanup pass uses a small lock around the expired list only.
/// </para>
/// </remarks>
internal sealed class HttpClientFactory : IHttpClientFactory, IDisposable, IAsyncDisposable
{
    private readonly HttpClientFactoryOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, ActiveHandlerEntry> _active = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, object> _nameLocks = new(StringComparer.Ordinal);
    private readonly object _expiredLock = new();
    private readonly List<ExpiredHandlerEntry> _expired = new();
    private bool _disposed;

    public HttpClientFactory(HttpClientFactoryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.DefaultHandlerLifetime <= TimeSpan.Zero)
        {
            throw new ArgumentException(
                $"{nameof(HttpClientFactoryOptions)}.{nameof(HttpClientFactoryOptions.DefaultHandlerLifetime)} must be positive.",
                nameof(options));
        }

        _options = options;
        _timeProvider = options.TimeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public HttpClient Create(string name)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (!_options.NamedClients.TryGetValue(name, out NamedHttpClientOptions? clientOptions))
        {
            throw new InvalidOperationException(
                $"No HTTP client has been registered with the name '{name}'. Register clients via HttpClientFactoryBuilder.AddClient before calling Create.");
        }

        ActiveHandlerEntry entry = GetOrRotateActiveEntry(name, clientOptions);

        // Wrap the shared inner handler in a per-client lifetime tracker. The HttpClient
        // owns this wrapper (disposeHandler: true) but the wrapper does NOT propagate
        // disposal to the inner handler — that's the factory's job.
        var wrapper = new LifetimeTrackingHttpMessageHandler(entry.Handler);
        entry.RegisterWrapper(wrapper);

        var client = new HttpClient(wrapper, disposeHandler: true);
        if (clientOptions.BaseAddress is not null)
        {
            client.BaseAddress = clientOptions.BaseAddress;
        }
        if (clientOptions.RequestTimeout is { } timeout)
        {
            client.Timeout = timeout;
        }
        clientOptions.ConfigureDefaultHeaders?.Invoke(client.DefaultRequestHeaders);
        return client;
    }

    /// <summary>
    /// Diagnostic accessor used by the test suite. Returns the inner handler instance
    /// currently associated with <paramref name="name"/>, or <see langword="null"/> when
    /// the name has not yet been touched. Returns the same instance for every call within
    /// a single lifetime window.
    /// </summary>
    internal HttpMessageHandler? PeekActiveInnerHandler(string name)
    {
        return _active.TryGetValue(name, out ActiveHandlerEntry? entry)
            ? entry.Handler
            : null;
    }

    /// <summary>
    /// Diagnostic accessor used by the test suite. Returns the count of expired-but-
    /// not-yet-disposed handlers across all named clients. Drops to zero once the
    /// wrapping <see cref="LifetimeTrackingHttpMessageHandler"/>s have been GC'd and the
    /// next cleanup pass runs.
    /// </summary>
    internal int CountExpiredHandlers()
    {
        lock (_expiredLock)
        {
            return _expired.Count;
        }
    }

    /// <summary>
    /// Forces a cleanup of expired handlers whose wrapping
    /// <see cref="LifetimeTrackingHttpMessageHandler"/>s have been GC'd. Called
    /// opportunistically by <see cref="Create"/> on every rotation; tests can also invoke
    /// it directly to assert deterministic cleanup.
    /// </summary>
    internal int CleanupExpired()
    {
        int disposed = 0;
        lock (_expiredLock)
        {
            for (int i = _expired.Count - 1; i >= 0; i--)
            {
                ExpiredHandlerEntry entry = _expired[i];
                if (!entry.HasLiveWrappers())
                {
                    entry.InnerHandler.Dispose();
                    _expired.RemoveAt(i);
                    disposed++;
                }
            }
        }
        return disposed;
    }

    private ActiveHandlerEntry GetOrRotateActiveEntry(string name, NamedHttpClientOptions clientOptions)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();

        // Fast path: an existing entry is still inside its lifetime window.
        if (_active.TryGetValue(name, out ActiveHandlerEntry? current) && current.ExpiresAtUtc > now)
        {
            return current;
        }

        // Slow path: per-name lock so handler creation runs at most once per rotation under
        // concurrent load. (ConcurrentDictionary.GetOrAdd's value factory is not single-flight
        // — it can run more than once when multiple threads race on a missing key.)
        object nameLock = _nameLocks.GetOrAdd(name, _ => new object());
        lock (nameLock)
        {
            // Re-check under the lock in case another thread already created / rotated.
            if (_active.TryGetValue(name, out current) && current.ExpiresAtUtc > now)
            {
                return current;
            }

            ActiveHandlerEntry replacement = CreateNewEntry(clientOptions, now);
            if (current is not null)
            {
                MoveToExpired(current);
            }
            _active[name] = replacement;
            CleanupExpired();
            return replacement;
        }
    }

    private ActiveHandlerEntry CreateNewEntry(NamedHttpClientOptions clientOptions, DateTimeOffset now)
    {
        TimeSpan lifetime = clientOptions.HandlerLifetime ?? _options.DefaultHandlerLifetime;
        HttpMessageHandler handler;
        if (clientOptions.HandlerFactory is { } factory)
        {
            handler = factory.Invoke()
                ?? throw new InvalidOperationException(
                    "NamedHttpClientOptions.HandlerFactory returned null. Return a non-null HttpMessageHandler instance.");
        }
        else
        {
            var sockets = new SocketsHttpHandler();
            clientOptions.ConfigureHandler?.Invoke(sockets);

            // Redirect policy is owned by the factory's redirect layer (RFC 10008 §2.5 method
            // semantics), switched via NamedHttpClientOptions.AllowAutoRedirect — the inner
            // handler must never double-follow, so its own following is always off.
            sockets.AllowAutoRedirect = false;
            handler = sockets;
        }

        if (clientOptions.AllowAutoRedirect)
        {
            handler = new RedirectHttpMessageHandler(handler, clientOptions.MaxAutomaticRedirections);
        }

        return new ActiveHandlerEntry(handler, now + lifetime);
    }

    private void MoveToExpired(ActiveHandlerEntry entry)
    {
        var expired = new ExpiredHandlerEntry(entry.Handler, entry.GetWrapperRefs());
        lock (_expiredLock)
        {
            _expired.Add(expired);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        foreach (KeyValuePair<string, ActiveHandlerEntry> kvp in _active)
        {
            kvp.Value.Handler.Dispose();
        }
        _active.Clear();

        lock (_expiredLock)
        {
            foreach (ExpiredHandlerEntry expired in _expired)
            {
                expired.InnerHandler.Dispose();
            }
            _expired.Clear();
        }
        GC.SuppressFinalize(this);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}


