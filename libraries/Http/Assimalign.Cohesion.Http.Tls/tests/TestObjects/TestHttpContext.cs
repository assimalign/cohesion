using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http.Tls.Tests.TestObjects;

/// <summary>
/// Minimal <see cref="IHttpContext"/> test double that exposes a real <see cref="Features"/>
/// collection and the connection info it was built with; the TLS accessor reads nothing else, so the
/// request and response throw to surface accidental use.
/// </summary>
internal sealed class TestHttpContext : IHttpContext
{
    public TestHttpContext(IHttpConnectionInfo connectionInfo)
    {
        ConnectionInfo = connectionInfo;
    }

    public IHttpConnectionInfo ConnectionInfo { get; }

    public IHttpFeatureCollection Features { get; } = new HttpFeatureCollection();

    public IDictionary<string, object?> Items { get; } = new Dictionary<string, object?>(StringComparer.Ordinal);

    public HttpVersion Version => HttpVersion.Http11;

    public IHttpRequest Request => throw new NotSupportedException();

    public IHttpResponse Response => throw new NotSupportedException();

    public CancellationToken RequestCancelled => CancellationToken.None;

    public void Cancel()
    {
    }

    public Task CancelAsync() => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
