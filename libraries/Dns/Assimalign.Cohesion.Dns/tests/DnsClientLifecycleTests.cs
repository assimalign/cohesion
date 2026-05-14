using System;
using System.Threading;
using System.Threading.Tasks;
using Assimalign.Cohesion.Dns;

namespace Assimalign.Cohesion.Dns.Tests;

/// <summary>
/// Covers the lifecycle plumbing shared by every <see cref="DnsClient"/> subclass: Dispose
/// idempotency, DisposeAsync delegation, and the ThrowIfDisposed guard. The point of the
/// abstract-class shape (vs the previous interface) is that this behavior is implemented once
/// in the base, so derived clients don't reinvent it.
/// </summary>
public class DnsClientLifecycleTests
{
    [Fact(DisplayName = "Cohesion Test [Dns] - Client: Dispose calls DisposeCore exactly once")]
    public void Dispose_Idempotent()
    {
        var client = new TestClient();
        client.Dispose();
        client.Dispose();
        Assert.Equal(1, client.DisposeCoreCalls);
    }

    [Fact(DisplayName = "Cohesion Test [Dns] - Client: DisposeAsync falls back to DisposeCore by default")]
    public async Task DisposeAsync_FallsBackToDisposeCore()
    {
        var client = new TestClient();
        await client.DisposeAsync();
        Assert.Equal(1, client.DisposeCoreCalls);
    }

    [Fact(DisplayName = "Cohesion Test [Dns] - Client: DisposeAsync after Dispose is a no-op")]
    public async Task DisposeAsync_AfterDispose_NoOp()
    {
        var client = new TestClient();
        client.Dispose();
        await client.DisposeAsync();
        Assert.Equal(1, client.DisposeCoreCalls);
    }

    [Fact(DisplayName = "Cohesion Test [Dns] - Client: ThrowIfDisposed surfaces ObjectDisposedException")]
    public void ThrowIfDisposed_Surfaces()
    {
        var client = new TestClient();
        client.Dispose();
        Assert.Throws<ObjectDisposedException>(() => client.AssertNotDisposed());
    }

    [Fact(DisplayName = "Cohesion Test [Dns] - Client: IsDisposed flips after Dispose")]
    public void IsDisposed_Reflects()
    {
        var client = new TestClient();
        Assert.False(client.IsDisposedPublic);
        client.Dispose();
        Assert.True(client.IsDisposedPublic);
    }

    [Fact(DisplayName = "Cohesion Test [Dns] - Client: DisposeAsyncCore override path is taken")]
    public async Task DisposeAsyncCore_OverridePath()
    {
        var client = new AsyncDisposalClient();
        await client.DisposeAsync();
        Assert.Equal(1, client.DisposeAsyncCoreCalls);
        Assert.Equal(0, client.DisposeCoreCalls);
    }

    private sealed class TestClient : DnsClient
    {
        public int DisposeCoreCalls { get; private set; }
        public bool IsDisposedPublic => IsDisposed;

        public override Task<DnsMessage> QueryAsync(DnsQuestion question, CancellationToken cancellationToken = default)
            => Task.FromResult(new DnsMessage(0, question));

        public void AssertNotDisposed() => ThrowIfDisposed();

        protected override void DisposeCore() => DisposeCoreCalls++;
    }

    private sealed class AsyncDisposalClient : DnsClient
    {
        public int DisposeCoreCalls { get; private set; }
        public int DisposeAsyncCoreCalls { get; private set; }

        public override Task<DnsMessage> QueryAsync(DnsQuestion question, CancellationToken cancellationToken = default)
            => Task.FromResult(new DnsMessage(0, question));

        protected override void DisposeCore() => DisposeCoreCalls++;
        protected override System.Threading.Tasks.ValueTask DisposeAsyncCore()
        {
            DisposeAsyncCoreCalls++;
            return System.Threading.Tasks.ValueTask.CompletedTask;
        }
    }
}
