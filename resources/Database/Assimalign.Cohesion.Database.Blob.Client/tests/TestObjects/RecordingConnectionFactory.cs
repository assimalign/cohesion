using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Database.Blob.Client.Tests;

internal sealed class RecordingConnectionFactory : DuplexConnectionFactory
{
    private readonly IDuplexConnectionFactory _inner;

    /// <summary>Initializes a new instance of the <see cref="RecordingConnectionFactory"/> class.</summary>
    /// <param name="inner">The connection factory whose connections are recorded.</param>
    public RecordingConnectionFactory(IDuplexConnectionFactory inner)
    {
        _inner = inner;
    }

    internal IDuplexConnection? LastConnection { get; private set; }
    public override ConnectionCapabilities Capabilities => _inner.Capabilities;

    protected override async ValueTask<IDuplexConnection> ConnectCoreAsync(EndPoint endPoint, CancellationToken cancellationToken = default)
    {
        IDuplexConnection connection = await _inner.ConnectAsync(endPoint, cancellationToken);
        LastConnection = connection;
        return connection;
    }
}
