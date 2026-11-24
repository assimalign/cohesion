using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.IdentityHub;

/// <summary>
/// The app registrations an identity hub knows: the ones defined in code, which are locked,
/// and the ones created while it runs, which are stored.
/// </summary>
/// <remarks>
/// <para>
/// <b>Code defines, the registry serves, and code wins.</b> A registration defined in code is
/// read like any other, carries <see cref="IdentityClient.IsLocked"/>, and is never written:
/// every write that names it is answered <see cref="IdentityClientWriteStatus.Locked"/>, and it
/// changes only where it is defined. A stored registration that has the identifier of one
/// defined in code is not served.
/// </para>
/// <para>
/// <b>Code adopts by identifier.</b> A registration created while the hub runs is stored and may
/// change. When a later deployment defines a registration with the same identifier, which
/// <see cref="IdentityClientId.FromName"/> makes the same for the same name, the registry serves
/// the one from code, locked, from then on. Adoption replaces the registration whole: the
/// stored one, with any secret issued to it, is no longer served.
/// </para>
/// <para>
/// <b>Writes are conditional and report their outcome.</b> A registration is immutable, so a
/// change is a replacement that names the version it was read at. A refused write is a result,
/// not an exception: an exception means the registry could not carry the operation out.
/// </para>
/// <para>
/// <see cref="IdentityClientRegistry"/> is the implementation, over an
/// <see cref="IIdentityHubStore"/>. Implementations are safe for concurrent use.
/// </para>
/// </remarks>
public interface IIdentityClientRegistry
{
    /// <summary>
    /// Finds the registration with an identifier.
    /// </summary>
    /// <param name="id">The registration's identifier: the OAuth <c>client_id</c>.</param>
    /// <param name="cancellationToken">The token that cancels the read.</param>
    /// <returns>The registration, or <see langword="null"/> when none has the identifier.</returns>
    /// <exception cref="ArgumentException"><paramref name="id"/> is <see langword="default"/>.</exception>
    /// <exception cref="IdentityHubStoreException">
    /// The store could not complete the read, or the stored registration could not be decoded.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    ValueTask<IdentityClient?> FindAsync(IdentityClientId id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds the registration that exposes an audience: the API a token for that audience is for.
    /// </summary>
    /// <param name="audience">The audience.</param>
    /// <param name="cancellationToken">The token that cancels the read.</param>
    /// <returns>The registration, or <see langword="null"/> when none exposes the audience.</returns>
    /// <exception cref="ArgumentException"><paramref name="audience"/> is <see langword="default"/>.</exception>
    /// <exception cref="IdentityHubStoreException">
    /// The store could not complete the read, or the stored registration could not be decoded.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    ValueTask<IdentityClient?> FindByAudienceAsync(IdentityClientAudience audience, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads one page of the registrations: the ones defined in code first, in the order of their
    /// identifiers, then the stored ones.
    /// </summary>
    /// <param name="limit">The most registrations the page holds: 1 to <see cref="IdentityClientPage.MaxLimit"/>.</param>
    /// <param name="continuation">
    /// The <see cref="IdentityClientPage.Continuation"/> of the page to continue after, or
    /// <see langword="null"/> for the first page.
    /// </param>
    /// <param name="cancellationToken">The token that cancels the read.</param>
    /// <returns>
    /// The page. Only a <see langword="null"/> <see cref="IdentityClientPage.Continuation"/> ends
    /// the enumeration: a page may hold fewer registrations than <paramref name="limit"/>, or none.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="limit"/> is out of range.</exception>
    /// <exception cref="ArgumentException"><paramref name="continuation"/> is empty.</exception>
    /// <exception cref="IdentityHubStoreException">
    /// The store could not complete the read; <paramref name="continuation"/> was not issued by
    /// this registry; or a stored registration could not be decoded.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    ValueTask<IdentityClientPage> ListAsync(
        int limit = IdentityClientPage.DefaultLimit,
        string? continuation = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores a new registration.
    /// </summary>
    /// <param name="client">
    /// The registration to store. Its identifier is its own to choose: one derived from a name
    /// with <see cref="IdentityClientId.FromName"/> can be adopted by code later, and a random
    /// one cannot.
    /// </param>
    /// <param name="cancellationToken">The token that cancels the write.</param>
    /// <returns>
    /// <see cref="IdentityClientWriteStatus.Written"/> with the stored version;
    /// <see cref="IdentityClientWriteStatus.Locked"/> when code defines a registration with the
    /// identifier; <see cref="IdentityClientWriteStatus.IdConflict"/> when a stored one has it; or
    /// <see cref="IdentityClientWriteStatus.AudienceConflict"/> when another registration exposes
    /// one of its audiences.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="client"/> is <see langword="null"/>.</exception>
    /// <exception cref="IdentityHubStoreException">The store could not complete the write.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    ValueTask<IdentityClientWriteResult> TryAddAsync(IdentityClient client, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces a stored registration, when its version is the expected one.
    /// </summary>
    /// <param name="client">The replacement, whose identifier names the registration to replace.</param>
    /// <param name="expectedVersion">The <see cref="IdentityClient.Version"/> the caller read.</param>
    /// <param name="cancellationToken">The token that cancels the write.</param>
    /// <returns>
    /// <see cref="IdentityClientWriteStatus.Written"/> with the new version;
    /// <see cref="IdentityClientWriteStatus.Locked"/> when code defines the registration;
    /// <see cref="IdentityClientWriteStatus.VersionConflict"/> when the stored registration changed
    /// or is gone; or <see cref="IdentityClientWriteStatus.AudienceConflict"/> when another
    /// registration exposes one of its audiences.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="client"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="expectedVersion"/> is <see langword="default"/>.</exception>
    /// <exception cref="IdentityHubStoreException">The store could not complete the write.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    ValueTask<IdentityClientWriteResult> TryReplaceAsync(
        IdentityClient client,
        IdentityHubRecordVersion expectedVersion,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a stored registration, when its version is the expected one.
    /// </summary>
    /// <remarks>
    /// Permissions other registrations hold on the removed one are left as they are and grant
    /// nothing from then on.
    /// </remarks>
    /// <param name="id">The identifier of the registration to remove.</param>
    /// <param name="expectedVersion">The <see cref="IdentityClient.Version"/> the caller read.</param>
    /// <param name="cancellationToken">The token that cancels the write.</param>
    /// <returns>
    /// <see cref="IdentityClientWriteStatus.Removed"/>;
    /// <see cref="IdentityClientWriteStatus.Locked"/> when code defines the registration; or
    /// <see cref="IdentityClientWriteStatus.VersionConflict"/> when the stored registration changed
    /// or is gone.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="id"/> or <paramref name="expectedVersion"/> is <see langword="default"/>.
    /// </exception>
    /// <exception cref="IdentityHubStoreException">The store could not complete the write.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    ValueTask<IdentityClientWriteResult> TryRemoveAsync(
        IdentityClientId id,
        IdentityHubRecordVersion expectedVersion,
        CancellationToken cancellationToken = default);
}
