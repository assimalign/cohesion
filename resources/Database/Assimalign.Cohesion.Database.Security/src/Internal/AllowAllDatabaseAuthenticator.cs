using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Security;

/// <summary>
/// The trust-everything authenticator behind <see cref="DatabaseAuthenticator.AllowAll"/>:
/// accepts any principal for any database without inspecting the evidence.
/// </summary>
internal sealed class AllowAllDatabaseAuthenticator : IDatabaseAuthenticator
{
    /// <inheritdoc />
    public ValueTask<bool> AuthenticateAsync(string database, string principal, ReadOnlyMemory<byte> evidence, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(true);
    }
}
