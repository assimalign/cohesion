using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Security;

namespace Assimalign.Cohesion.Database.Blob.Tests;

/// <summary>
/// An authenticator whose verification throws, an infrastructure failure the server session
/// cannot classify: the fault the event-source tests inject into the handshake exchange.
/// </summary>
internal sealed class FaultingAuthenticator : DatabaseAuthenticator
{
    public const string FaultMessage = "The authenticator's backing store is unreachable.";

    protected override ValueTask<bool> AuthenticateCoreAsync(string database, string principal, ReadOnlyMemory<byte> evidence, CancellationToken cancellationToken)
        => throw new InvalidOperationException(FaultMessage);
}
