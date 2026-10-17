using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Security.Internal;

/// <summary>
/// The trust-everything authenticator behind <see cref="DatabaseAuthenticator.AllowAll"/>:
/// accepts any principal for any database without inspecting the evidence.
/// </summary>
internal sealed class AllowAllDatabaseAuthenticator : DatabaseAuthenticator
{
    /// <inheritdoc />
    protected override ValueTask<bool> AuthenticateCoreAsync(string database, string principal, ReadOnlyMemory<byte> evidence, CancellationToken cancellationToken)
        => ValueTask.FromResult(true);
}
