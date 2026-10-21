using Assimalign.Cohesion.Database.Indexing;
using Assimalign.Cohesion.Database.Sql.Catalog;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>A statement's registered index, live tree, and bound key ordinals.</summary>
internal readonly record struct SqlLiveIndex(SqlCatalogIndex Metadata, BTreeIndex Index, int[] KeyOrdinals)
{
    internal BTreeRecordVersionIndex Versions { get; } = BTreeRecordVersionIndex.Create(Index);
}
