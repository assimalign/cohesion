using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http.ExtendedConnect.Tests.TestObjects;

/// <summary>
/// Minimal <see cref="IHttpContext"/> test double that exposes a real
/// <see cref="Items"/> bag; the extended CONNECT accessors read nothing else,
/// so the remaining members throw to surface accidental use.
/// </summary>
internal sealed class FakeHttpContext : IHttpContext
{
    public IDictionary<string, object?> Items { get; } = new Dictionary<string, object?>(StringComparer.Ordinal);

    public HttpVersion Version => default;

    public IHttpRequest Request => throw new NotSupportedException();

    public IHttpResponse Response => throw new NotSupportedException();

    public IHttpConnectionInfo ConnectionInfo => throw new NotSupportedException();

    public IHttpFeatureCollection Features => throw new NotSupportedException();

    public CancellationToken RequestCancelled => CancellationToken.None;

    public void Cancel()
    {
    }

    public Task CancelAsync() => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
