using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Security;

namespace Assimalign.Cohesion.Database.Blob.Tests;

/// <summary>
/// An authenticator that rejects every principal, for handshake-failure tests.
/// </summary>
internal sealed class RejectingAuthenticator : DatabaseAuthenticator
{
    protected override ValueTask<bool> AuthenticateCoreAsync(string database, string principal, ReadOnlyMemory<byte> evidence, CancellationToken cancellationToken)
        => ValueTask.FromResult(false);
}
