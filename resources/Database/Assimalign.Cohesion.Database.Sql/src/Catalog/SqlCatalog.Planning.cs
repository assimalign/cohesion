namespace Assimalign.Cohesion.Database.Sql.Catalog;

public sealed partial class SqlCatalog
{
    // Planning's ordinary join fast path must not allocate GetIndexes' result list
    // merely to discover that there is no partial index. No metadata is exposed.
    internal bool ContainsPartialIndex(ulong tableObjectId)
    {
        lock (_sync)
        {
            ThrowIfOffline();
            foreach (var entry in _indexes)
            {
                if (entry.Value.Index.TableObjectId == tableObjectId && entry.Value.Index.Predicate is not null) { return true; }
            }
            return false;
        }
    }
}
