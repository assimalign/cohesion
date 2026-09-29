using System;

// Deviates from the repo namespace-matches-assembly rule per design decision:
// session contracts retain the established HTTP family namespace.
namespace Assimalign.Cohesion.Http;

/// <summary>
/// A buffered HTTP session whose state is loaded from and committed to an
/// <see cref="IHttpSessionStore"/>.
/// </summary>
/// <remarks>
/// Create a session through <see cref="HttpSessionStoreExtensions"/>. Commits
/// write modified state using the store's last-commit-wins contract; an unmodified,
/// loaded session refreshes its idle window. Instances are scoped to one exchange
/// and are not thread-safe. Cookie handling belongs to the hosting pipeline.
/// </remarks>
public interface IHttpStoredSession : IHttpSession
{
    /// <summary>
    /// Changes the identifier while preserving buffered state and marks the
    /// session modified so its next commit writes under the new identifier.
    /// </summary>
    /// <param name="newId">The nonempty replacement session identifier.</param>
    /// <remarks>
    /// This operation performs no store I/O. The caller must remove the old
    /// identifier from the store and deliver the replacement identifier to the
    /// client when implementing session-id regeneration.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="newId"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="newId"/> is empty.</exception>
    void ReassignId(string newId);
}
