using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Security;

namespace Assimalign.Cohesion.Database.Sql.Client.Tests;

internal sealed class TerminalRejectingAuthenticator : DatabaseAuthenticator
{
    protected override ValueTask<bool> AuthenticateCoreAsync(string database, string principal, ReadOnlyMemory<byte> evidence, CancellationToken cancellationToken)
        => ValueTask.FromResult(false);
}
