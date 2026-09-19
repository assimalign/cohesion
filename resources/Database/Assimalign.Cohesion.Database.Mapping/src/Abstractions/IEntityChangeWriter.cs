using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Mapping;

/// <summary>Stages a captured entity change in a model-specific transaction.</summary>
/// <typeparam name="TKey">The identity value.</typeparam>
/// <typeparam name="TSnapshot">The mapping's immutable snapshot.</typeparam>
/// <typeparam name="TTransaction">The adapter's transaction contract.</typeparam>
public interface IEntityChangeWriter<TKey, TSnapshot, in TTransaction>
    where TKey : notnull
    where TTransaction : class, IMappingTransaction
{
    /// <summary>Stages a change without committing or mutating its snapshots.</summary>
    /// <param name="transaction">The shared transaction for this save.</param>
    /// <param name="change">The captured change; original and current allow model-specific partial updates.</param>
    /// <param name="cancellationToken">Cancels staging.</param>
    /// <returns>The asynchronous staging operation.</returns>
    ValueTask ApplyAsync(TTransaction transaction, EntityChange<TKey, TSnapshot> change, CancellationToken cancellationToken = default);
}
