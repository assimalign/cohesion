using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Mapping;

/// <summary>Owns one atomic save transaction.</summary>
/// <remarks>Disposal must roll back every staged write unless commit succeeded. Commit must publish
/// all writes atomically; a throwing or cancelled commit must publish none. Adapters with ambiguous
/// commit outcomes cannot implement this contract. Disposal after a successful commit must not undo it.</remarks>
public interface IMappingTransaction : IAsyncDisposable
{
    /// <summary>Atomically publishes every staged change.</summary>
    /// <param name="cancellationToken">Cancels before publication.</param>
    /// <returns>The asynchronous commit operation.</returns>
    ValueTask CommitAsync(CancellationToken cancellationToken = default);
}
