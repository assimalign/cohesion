using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Mapping;

internal interface IPendingChange<in TTransaction> where TTransaction : class, IMappingTransaction
{
    ValueTask ApplyAsync(TTransaction transaction, CancellationToken cancellationToken = default);
    void Accept();
}
