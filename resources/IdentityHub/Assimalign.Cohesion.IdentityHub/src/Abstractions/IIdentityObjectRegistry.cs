using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.IdentityHub;

/// <summary>
/// The identity objects of one kind that an identity hub knows: the ones defined in code, which
/// are locked, and the ones created while it runs, which are stored.
/// </summary>
/// <remarks>
/// <para>
/// <b>Code defines, the registry serves, and code wins.</b> An object defined in code is read
/// like any other, reports that it is locked, and is never written: every write that names it
/// is answered <see cref="IdentityObjectWriteStatus.Locked"/>, and it changes only where it is
/// defined. A stored object that has the identifier of one defined in code is not served, and a
/// name code holds is found only as the object from code.
/// </para>
/// <para>
/// <b>Code adopts by identifier.</b> An object created while the hub runs is stored and may
/// change. When a later deployment defines an object with the same identifier, which deriving
/// it from a name makes the same for the same name, the registry serves the one from code,
/// locked, from then on. Adoption replaces the object whole. The stored record is left in the
/// store and is served again if the code that shadows it is removed.
/// </para>
/// <para>
/// <b>Writes are conditional and report their outcome.</b> An identity object is immutable, so
/// a change is a replacement that names the version it was read at. A refused write is a
/// result, not an exception: an exception means the registry could not carry the operation out.
/// </para>
/// <para>
/// Implementations are safe for concurrent use.
/// </para>
/// </remarks>
/// <typeparam name="TObject">The identity object the registry serves.</typeparam>
/// <typeparam name="TId">The identifier of that kind of object.</typeparam>
public interface IIdentityObjectRegistry<TObject, TId>
    where TObject : class
    where TId : struct
{
    /// <summary>
    /// Gets the objects defined in code, in the order of their identifiers. Each is locked.
    /// </summary>
    IReadOnlyList<TObject> Locked { get; }

    /// <summary>
    /// Finds the object with an identifier.
    /// </summary>
    /// <param name="id">The object's identifier.</param>
    /// <param name="cancellationToken">The token that cancels the read.</param>
    /// <returns>The object, or <see langword="null"/> when none has the identifier.</returns>
    /// <exception cref="ArgumentException"><paramref name="id"/> is <see langword="default"/>.</exception>
    /// <exception cref="IdentityHubStoreException">
    /// The store could not complete the read, or the stored object could not be decoded.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    ValueTask<TObject?> FindAsync(TId id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads one page of the objects: the ones defined in code first, in the order of their
    /// identifiers, then the stored ones.
    /// </summary>
    /// <param name="limit">The most objects the page holds: 1 to <see cref="IdentityHubRecordQuery.MaxLimit"/>.</param>
    /// <param name="continuation">
    /// The <see cref="IdentityObjectPage{TObject}.Continuation"/> of the page to continue after,
    /// or <see langword="null"/> for the first page.
    /// </param>
    /// <param name="cancellationToken">The token that cancels the read.</param>
    /// <returns>
    /// The page. Only a <see langword="null"/> <see cref="IdentityObjectPage{TObject}.Continuation"/>
    /// ends the enumeration: a page may hold fewer objects than <paramref name="limit"/>, or none.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="limit"/> is out of range.</exception>
    /// <exception cref="ArgumentException"><paramref name="continuation"/> is empty.</exception>
    /// <exception cref="IdentityHubStoreException">
    /// The store could not complete the read; <paramref name="continuation"/> was not issued by
    /// this registry; or a stored object could not be decoded.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    ValueTask<IdentityObjectPage<TObject>> ListAsync(
        int limit = IdentityHubRecordQuery.DefaultLimit,
        string? continuation = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores a new object.
    /// </summary>
    /// <param name="value">The object to store.</param>
    /// <param name="cancellationToken">The token that cancels the write.</param>
    /// <returns>
    /// <see cref="IdentityObjectWriteStatus.Written"/> with the stored version;
    /// <see cref="IdentityObjectWriteStatus.Locked"/> when code defines an object with the
    /// identifier; <see cref="IdentityObjectWriteStatus.IdConflict"/> when a stored one has it; or
    /// <see cref="IdentityObjectWriteStatus.NameConflict"/> when another object holds the name.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="IdentityHubStoreException">The store could not complete the write.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    ValueTask<IdentityObjectWriteResult> TryAddAsync(TObject value, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces a stored object, when its version is the expected one.
    /// </summary>
    /// <param name="value">The replacement, whose identifier names the object to replace.</param>
    /// <param name="expectedVersion">The version the caller read the object at.</param>
    /// <param name="cancellationToken">The token that cancels the write.</param>
    /// <returns>
    /// <see cref="IdentityObjectWriteStatus.Written"/> with the new version;
    /// <see cref="IdentityObjectWriteStatus.Locked"/> when code defines the object;
    /// <see cref="IdentityObjectWriteStatus.VersionConflict"/> when the stored object changed or
    /// is gone; or <see cref="IdentityObjectWriteStatus.NameConflict"/> when another object holds
    /// the name.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="expectedVersion"/> is <see langword="default"/>.</exception>
    /// <exception cref="IdentityHubStoreException">The store could not complete the write.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    ValueTask<IdentityObjectWriteResult> TryReplaceAsync(
        TObject value,
        IdentityHubRecordVersion expectedVersion,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a stored object, when its version is the expected one.
    /// </summary>
    /// <remarks>
    /// What other objects say about the removed one is left as it is and applies to nothing from
    /// then on.
    /// </remarks>
    /// <param name="id">The identifier of the object to remove.</param>
    /// <param name="expectedVersion">The version the caller read the object at.</param>
    /// <param name="cancellationToken">The token that cancels the write.</param>
    /// <returns>
    /// <see cref="IdentityObjectWriteStatus.Removed"/>;
    /// <see cref="IdentityObjectWriteStatus.Locked"/> when code defines the object; or
    /// <see cref="IdentityObjectWriteStatus.VersionConflict"/> when the stored object changed or
    /// is gone.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="id"/> or <paramref name="expectedVersion"/> is <see langword="default"/>.
    /// </exception>
    /// <exception cref="IdentityHubStoreException">The store could not complete the write.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    ValueTask<IdentityObjectWriteResult> TryRemoveAsync(
        TId id,
        IdentityHubRecordVersion expectedVersion,
        CancellationToken cancellationToken = default);
}
