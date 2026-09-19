using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Mapping;

/// <summary>Owns one atomic save transaction.</summary>
/// <remarks>Disposal must roll back every staged write unless commit succeeded. Commit must publish
/// all writes atomically; an ordinary throwing or cancelled commit must publish none. If a transport
/// failure prevents determining whether publication occurred, the adapter must instead throw
/// <see cref="MappingCommitOutcomeUnknownException"/> and prevent reuse of the unresolved store scope.
/// The unit of work then rejects all further use; rollback and safe retry are not promised.
/// Disposal after a successful commit must not undo it.</remarks>
public interface IMappingTransaction : IAsyncDisposable
{
    /// <summary>Atomically publishes every staged change.</summary>
    /// <param name="cancellationToken">Cancels before publication.</param>
    /// <returns>The asynchronous commit operation.</returns>
    /// <exception cref="MappingCommitOutcomeUnknownException">Publication may have occurred but cannot be confirmed.</exception>
    ValueTask CommitAsync(CancellationToken cancellationToken = default);
}
