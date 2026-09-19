using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Mapping;

internal interface ITrackedMapping<TTransaction> where TTransaction : class, IMappingTransaction
{
    void Prepare(List<IPendingChange<TTransaction>> changes);
}
