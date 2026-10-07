using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http.ExtendedConnect.Tests.TestObjects;

/// <summary>
/// An <see cref="IHttpExtendedConnectFeature"/> double standing in for the transport's implementation:
/// it reports a fixed protocol and hands out a fixed tunnel stream, counting the accept calls.
/// </summary>
internal sealed class FakeExtendedConnectFeature : IHttpExtendedConnectFeature
{
    public FakeExtendedConnectFeature(string protocol, Stream tunnel)
    {
        Protocol = protocol;
        Tunnel = tunnel;
    }

    public string Name => "Assimalign.Cohesion.Http.ExtendedConnect";

    public string Protocol { get; }

    public Stream Tunnel { get; }

    public int AcceptCount { get; private set; }

    public ValueTask<Stream> AcceptAsync(CancellationToken cancellationToken = default)
    {
        AcceptCount++;
        return ValueTask.FromResult(Tunnel);
    }
}
