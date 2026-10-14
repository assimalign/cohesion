using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http.Internal;

/// <summary>
/// An <see cref="IHttpSession"/> whose state round-trips through an
/// <see cref="IHttpSessionStore"/>. It buffers the session dictionary in memory
/// for the exchange, hydrates it from the store on <see cref="LoadAsync"/>, and
/// flushes it back on <see cref="CommitAsync"/> — persisting only when the
/// session was actually modified, otherwise sliding the store's idle window.
/// </summary>
/// <remarks>
/// This is the session the Web session middleware installs over the configured
/// store; it is internal because consumers interact with it only through
/// <see cref="IHttpStoredSession"/> and the <c>UseSessions</c> pipeline. Framing is
/// delegated to <see cref="HttpSessionSerializer"/> so the exact bytes are
/// backend-independent.
/// </remarks>
internal sealed class HttpSessionStoreSession : IHttpStoredSession
{
    private readonly Dictionary<string, byte[]> _values = new(StringComparer.Ordinal);
    private readonly IHttpSessionStore _store;
    private readonly TimeSpan _idleTimeout;

    private string _id;
    private bool _isAvailable;
    private bool _isLoaded;
    private bool _isModified;

    public HttpSessionStoreSession(string id, IHttpSessionStore store, TimeSpan idleTimeout)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentNullException.ThrowIfNull(store);

        _id = id;
        _store = store;
        _idleTimeout = idleTimeout;
    }

    /// <inheritdoc />
    public bool IsAvailable => _isAvailable;

    /// <inheritdoc />
    public string Id => _id;

    /// <summary>
    /// Gets a value indicating whether the session was mutated since it was
    /// loaded (or created). A commit persists only when this is
    /// <see langword="true"/>; otherwise it slides the store's idle window.
    /// </summary>
    public bool IsModified => _isModified;

    /// <inheritdoc />
    public IEnumerable<string> Keys => _values.Keys;

    /// <inheritdoc />
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        byte[]? frame;
        try
        {
            frame = await _store.GetAsync(_id, cancellationToken).ConfigureAwait(false);
        }
        // Store implementations define their own exception types. Observe and rethrow
        // at this boundary; a caller-requested cancellation is not a store failure.
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            HttpSessionEventSource.Log.StoreLoadFailed(exception);
            throw;
        }

        _values.Clear();
        if (frame is not null && HttpSessionSerializer.TryDeserialize(frame, out Dictionary<string, byte[]>? loaded))
        {
            foreach (KeyValuePair<string, byte[]> entry in loaded)
            {
                _values[entry.Key] = entry.Value;
            }
        }

        _isLoaded = true;
        _isModified = false;
        _isAvailable = true;
    }

    /// <inheritdoc />
    public async Task CommitAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (IsModified)
            {
                byte[] frame = HttpSessionSerializer.Serialize(_values);
                await _store.SetAsync(_id, frame, _idleTimeout, cancellationToken).ConfigureAwait(false);
                _isModified = false;
            }
            else if (_isLoaded)
            {
                // Skip the payload commit but retain renew-on-access for readers.
                await _store.RefreshAsync(_id, _idleTimeout, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            HttpSessionEventSource.Log.StoreCommitFailed(exception);
            throw;
        }
    }

    /// <inheritdoc />
    public bool TryGetValue(string key, [NotNullWhen(true)] out byte[]? value)
    {
        return _values.TryGetValue(key, out value);
    }

    /// <inheritdoc />
    public void Set(string key, byte[] value)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(value);

        _values[key] = value;
        _isModified = true;
    }

    /// <inheritdoc />
    public void Remove(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        if (_values.Remove(key))
        {
            _isModified = true;
        }
    }

    /// <inheritdoc />
    public void Clear()
    {
        if (_values.Count > 0)
        {
            _values.Clear();
            _isModified = true;
        }
    }

    /// <summary>
    /// Reassigns the session id, keeping the buffered state and marking the
    /// session modified so the next commit writes the state under the new id.
    /// This is the primitive behind session-id regeneration (post-authentication
    /// fixation defense); the caller is responsible for removing the old id from
    /// the store and re-issuing the cookie.
    /// </summary>
    /// <param name="newId">The new session identifier.</param>
    /// <exception cref="ArgumentException"><paramref name="newId"/> is <see langword="null"/> or empty.</exception>
    public void ReassignId(string newId)
    {
        ArgumentException.ThrowIfNullOrEmpty(newId);

        _id = newId;
        _isModified = true;
    }
}
