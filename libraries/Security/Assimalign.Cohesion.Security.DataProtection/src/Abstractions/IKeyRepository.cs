using System.Collections.Generic;

namespace Assimalign.Cohesion.Security.DataProtection;

/// <summary>
/// Persists the key ring's keys as opaque <see cref="KeyDocument"/> blobs. This is the seam
/// that makes keys survive restarts and be shared across nodes: the default implementation is
/// file-system backed (<see cref="KeyRepository.CreateFileSystem(string)"/>); a
/// secret-store-backed implementation is a planned follow-up.
/// </summary>
/// <remarks>
/// Implementations are pure blob stores and must not interpret document content. They are
/// expected to be safe for concurrent readers; the key ring serializes its own writes.
/// Because v1 documents contain key material in the clear, the store is responsible for
/// at-rest protection appropriate to its medium (for the file system, that means directory
/// permissions until at-rest ring encryption lands).
/// </remarks>
public interface IKeyRepository
{
    /// <summary>
    /// Returns every key document currently held by the repository, in no particular order.
    /// </summary>
    /// <returns>The stored key documents; an empty list when the repository is empty.</returns>
    IReadOnlyList<KeyDocument> GetAllKeys();

    /// <summary>
    /// Persists <paramref name="key"/>, replacing any existing document with the same
    /// <see cref="KeyDocument.Name"/>. The write should be durable and atomic enough that a
    /// concurrent reader never observes a partially written document.
    /// </summary>
    /// <param name="key">The key document to store.</param>
    void StoreKey(KeyDocument key);
}
