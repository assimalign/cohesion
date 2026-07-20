using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Sessions.Tests.TestObjects;

/// <summary>
/// A stand-in <see cref="IHttpResponseStreamingFeature"/> that reports a chosen
/// <see cref="HasStarted"/> value, so tests can exercise the head-committed guard
/// (cookie establishment is skipped once the head has started). Only
/// <see cref="HasStarted"/> is functional.
/// </summary>
internal sealed class FakeResponseStreamingFeature : IHttpResponseStreamingFeature
{
    public FakeResponseStreamingFeature(bool hasStarted) => HasStarted = hasStarted;

    public string Name => nameof(IHttpResponseStreamingFeature);

    public bool HasStarted { get; }

    public ValueTask StartAsync(CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public ValueTask FlushAsync(CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public ValueTask CompleteAsync(CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
}
