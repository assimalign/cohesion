using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Mapping;

/// <summary>Starts transactions for one store's atomic save boundary.</summary>
/// <typeparam name="TTransaction">The adapter's statically selected transaction contract.</typeparam>
public interface IMappingStore<TTransaction> where TTransaction : class, IMappingTransaction
{
    /// <summary>Starts a transaction owned and disposed by the unit of work.</summary>
    /// <param name="cancellationToken">Cancels transaction creation.</param>
    /// <returns>A transaction that has not published any writes.</returns>
    ValueTask<TTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default);
}
