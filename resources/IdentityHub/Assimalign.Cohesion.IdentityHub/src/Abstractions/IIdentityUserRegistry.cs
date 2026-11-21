using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.IdentityHub;

/// <summary>
/// The users an identity hub knows: the ones defined in code, which are locked, and the ones
/// created while it runs, which are stored.
/// </summary>
/// <remarks>
/// No two users hold one name. A name code holds is refused to a stored user, and a write that
/// asks for a name another stored user holds is answered
/// <see cref="IdentityObjectWriteStatus.NameConflict"/>.
/// </remarks>
public interface IIdentityUserRegistry : IIdentityObjectRegistry<IdentityUser, IdentityUserId>
{
    /// <summary>
    /// Finds the user that signs in with a name.
    /// </summary>
    /// <param name="name">The name. It compares case-insensitively.</param>
    /// <param name="cancellationToken">The token that cancels the read.</param>
    /// <returns>The user, or <see langword="null"/> when none holds the name.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is <see langword="default"/>.</exception>
    /// <exception cref="IdentityHubStoreException">
    /// The store could not complete the read, or the stored user could not be decoded.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    ValueTask<IdentityUser?> FindByNameAsync(IdentityUserName name, CancellationToken cancellationToken = default);
}
