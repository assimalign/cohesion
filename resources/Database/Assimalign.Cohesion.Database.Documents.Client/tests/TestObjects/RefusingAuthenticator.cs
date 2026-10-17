using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Security;

namespace Assimalign.Cohesion.Database.Documents.Client.Tests;

internal sealed class RefusingAuthenticator : DatabaseAuthenticator
{
    internal int Calls { get; private set; }
    internal string? Database { get; private set; }
    internal string? Principal { get; private set; }

    protected override ValueTask<bool> AuthenticateCoreAsync(string database, string principal, ReadOnlyMemory<byte> evidence, CancellationToken cancellationToken)
    {
        Calls++;
        Database = database;
        Principal = principal;
        return ValueTask.FromResult(false);
    }
}
