using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Security;

namespace Assimalign.Cohesion.Database.KeyValuePair.Tests;

/// <summary>
/// An authenticator that never answers but honors the handshake's cancellation, as one waiting on
/// a slow identity store does: the server's authentication timeout lapses inside it.
/// </summary>
internal sealed class StallingAuthenticator : DatabaseAuthenticator
{
    protected override async ValueTask<bool> AuthenticateCoreAsync(string database, string principal, ReadOnlyMemory<byte> evidence, CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        return false;
    }
}
