using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http.ProtocolUpgrade.Tests.TestObjects;

/// <summary>
/// Minimal <see cref="IHttpContext"/> test double exposing a real <see cref="Features"/>
/// collection; the <c>context.Upgrade</c> accessor reads nothing else, so the remaining members
/// throw to surface accidental use.
/// </summary>
internal sealed class FakeHttpContext : IHttpContext
{
    /// <inheritdoc />
    public IHttpFeatureCollection Features { get; } = new HttpFeatureCollection();

    /// <inheritdoc />
    public IDictionary<string, object?> Items { get; } = new Dictionary<string, object?>(StringComparer.Ordinal);

    /// <inheritdoc />
    public HttpVersion Version => HttpVersion.Http11;

    /// <inheritdoc />
    public IHttpRequest Request => throw new NotSupportedException();

    /// <inheritdoc />
    public IHttpResponse Response => throw new NotSupportedException();

    /// <inheritdoc />
    public IHttpConnectionInfo ConnectionInfo => throw new NotSupportedException();

    /// <inheritdoc />
    public CancellationToken RequestCancelled => CancellationToken.None;

    /// <inheritdoc />
    public void Cancel()
    {
    }

    /// <inheritdoc />
    public Task CancelAsync() => Task.CompletedTask;

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
