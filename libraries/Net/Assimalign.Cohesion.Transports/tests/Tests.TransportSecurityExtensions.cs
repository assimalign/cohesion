using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Net;
using System.Threading;

using Xunit;

namespace Assimalign.Cohesion.Transports.Tests;

public class TransportSecurityExtensionsTests
{
    [Fact]
    public void IsSecure_OnFreshContext_ShouldReturnFalse()
    {
        TestContext context = new();

        Assert.False(context.IsSecure);
    }

    [Fact]
    public void IsSecure_OnSetTrue_ShouldReturnTrueOnSubsequentRead()
    {
        TestContext context = new();

        context.IsSecure = true;

        Assert.True(context.IsSecure);
    }

    [Fact]
    public void IsSecure_OnSetTrueThenFalse_ShouldRoundTrip()
    {
        TestContext context = new();

        context.IsSecure = true;
        context.IsSecure = false;

        Assert.False(context.IsSecure);
    }

    [Fact]
    public void IsSecure_OnSet_ShouldWriteThroughToItemsByKey()
    {
        TestContext context = new();

        context.IsSecure = true;

        Assert.True(context.Items.ContainsKey(TransportSecurityExtensions.IsSecureItemKey));
        Assert.Equal(true, context.Items[TransportSecurityExtensions.IsSecureItemKey]);
    }

    [Fact]
    public void IsSecure_OnItemSetByOtherCaller_ShouldReturnRecordedValue()
    {
        TestContext context = new();
        context.Items[TransportSecurityExtensions.IsSecureItemKey] = true;

        Assert.True(context.IsSecure);
    }

    [Fact]
    public void IsSecure_OnItemSetToNonBoolean_ShouldReturnFalse()
    {
        // Defensive: a buggy consumer that stuffs a non-bool under the
        // key must not crash callers; the getter should treat it as
        // "secure flag not recorded".
        TestContext context = new();
        context.Items[TransportSecurityExtensions.IsSecureItemKey] = "not-a-bool";

        Assert.False(context.IsSecure);
    }

    private sealed class TestContext : ITransportConnectionContext
    {
        public EndPoint LocalEndPoint { get; } = new IPEndPoint(IPAddress.Loopback, 0);
        public EndPoint RemoteEndPoint { get; } = new IPEndPoint(IPAddress.Loopback, 0);
        public ITransportConnectionPipe Pipe { get; } = new TestPipe();
        public CancellationToken ConnectionCancelled { get; } = CancellationToken.None;
        public IDictionary<string, object?> Items { get; } = new Dictionary<string, object?>();
    }

    private sealed class TestPipe : ITransportConnectionPipe
    {
        private readonly Pipe _pipe = new();

        public PipeReader Input => _pipe.Reader;
        public PipeWriter Output => _pipe.Writer;

        public Stream GetStream() => Stream.Null;
    }
}
