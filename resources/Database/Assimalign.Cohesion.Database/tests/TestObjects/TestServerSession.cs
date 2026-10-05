using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Tests;

/// <summary>
/// A server session whose engine session a test sets, exposing the base's handshake setters.
/// </summary>
internal sealed class TestServerSession : DatabaseServerSession
{
    private int _disposeCores;

    public DatabaseSession? Session { get; set; }

    public int DisposeCores => Volatile.Read(ref _disposeCores);

    public override DatabaseSession? DatabaseSession => Session;

    public void Negotiate(ProtocolVersion version) => SetNegotiatedVersion(version);

    public void Authenticate(string principal) => SetAuthenticatedPrincipal(principal);

    protected override ValueTask DisposeAsyncCore()
    {
        Interlocked.Increment(ref _disposeCores);
        return ValueTask.CompletedTask;
    }
}
