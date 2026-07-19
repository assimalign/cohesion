using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Sessions.Tests.TestObjects;

/// <summary>
/// An <see cref="IHttpSessionStore"/> decorator over <see cref="InMemoryHttpSessionStore"/> that
/// counts writes, so tests can assert whether the middleware's commit path actually persisted
/// anything (for example, that an orphaned new session — one whose cookie could not be delivered —
/// is never written to the store).
/// </summary>
internal sealed class RecordingSessionStore : IHttpSessionStore
{
    private readonly InMemoryHttpSessionStore _inner = new();

    public int SetCount { get; private set; }

    public int RefreshCount { get; private set; }

    public ValueTask<byte[]?> GetAsync(string sessionId, CancellationToken cancellationToken = default)
        => _inner.GetAsync(sessionId, cancellationToken);

    public ValueTask SetAsync(string sessionId, byte[] payload, TimeSpan idleTimeout, CancellationToken cancellationToken = default)
    {
        SetCount++;
        return _inner.SetAsync(sessionId, payload, idleTimeout, cancellationToken);
    }

    public ValueTask RefreshAsync(string sessionId, TimeSpan idleTimeout, CancellationToken cancellationToken = default)
    {
        RefreshCount++;
        return _inner.RefreshAsync(sessionId, idleTimeout, cancellationToken);
    }

    public ValueTask RemoveAsync(string sessionId, CancellationToken cancellationToken = default)
        => _inner.RemoveAsync(sessionId, cancellationToken);
}
