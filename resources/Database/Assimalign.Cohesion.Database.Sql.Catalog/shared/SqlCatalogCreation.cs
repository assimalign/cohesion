using System.Linq;
using Assimalign.Cohesion.Database.Sql.Storage;
using Assimalign.Cohesion.Database.Types;

// Deviates from the repo namespace-matches-assembly rule: this stateless catalog predicate is shared with SQL through the existing catalog shared-source link, without widening the catalog API.
namespace Assimalign.Cohesion.Database.Sql.Catalog.Internal;

internal static class SqlCatalogCreation
{
    internal const int CounterRecordKind = 2;
    internal const int CollationRecordKind = 7;
    internal const int NamespaceRecordKind = 15;

    // Only creation's counter, optional collation and dbo may precede the final marker.
    internal static bool IsCreationOnly(SqlStorage storage, SqlCatalog catalog)
    {
        if (catalog.RecordSpaceFormatVersion != 1 || catalog.Namespaces.Any(schema => schema.Name != "dbo" || schema.Owner != DatabaseObjectOwner.Adhoc || schema.OwningSchema is not null)) { return false; }
        using var iterator = storage.GetUnitIterator();
        while (iterator.MoveNext())
        {
            var reader = new DatabaseKeyReader(iterator.Current.Data.Span);
            int kind = reader.ReadInt32();
            if (kind == CounterRecordKind)
            {
                if (reader.ReadInt64() != 1) { return false; }
                long generation = reader.ReadInt64();
                // Collation retries can durably advance this counter before the final marker.
                if (generation < 0 || !reader.IsAtEnd) { return false; }
            }
            else if (kind is not (CollationRecordKind or NamespaceRecordKind)) { return false; }
        }
        return true;
    }
}
