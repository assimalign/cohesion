using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Security;

namespace Assimalign.Cohesion.Database.Sql.Tests;

/// <summary>
/// An authenticator that rejects every principal, for handshake-failure tests.
/// </summary>
internal sealed class RejectingAuthenticator : IDatabaseAuthenticator
{
    public ValueTask<bool> AuthenticateAsync(string database, string principal, ReadOnlyMemory<byte> evidence, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(false);
}
