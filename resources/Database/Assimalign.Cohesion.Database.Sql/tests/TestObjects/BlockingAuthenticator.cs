using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Security;

namespace Assimalign.Cohesion.Database.Sql.Tests;

/// <summary>
/// An authenticator that holds the handshake until the test releases it, ignoring the
/// handshake's cancellation, so a stopping server's drain budget lapses with the session busy.
/// </summary>
internal sealed class BlockingAuthenticator : DatabaseAuthenticator
{
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes once the server called the authenticator.</summary>
    public Task Entered => _entered.Task;

    /// <summary>Lets the held verification return.</summary>
    /// <param name="authenticated">The verdict it returns.</param>
    public void Release(bool authenticated) => _release.TrySetResult(authenticated);

    protected override async ValueTask<bool> AuthenticateCoreAsync(string database, string principal, ReadOnlyMemory<byte> evidence, CancellationToken cancellationToken)
    {
        _entered.TrySetResult();
        return await _release.Task.ConfigureAwait(false);
    }
}
