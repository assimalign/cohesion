using Assimalign.Cohesion.Database.Indexing;
using Assimalign.Cohesion.Database.Sql.Catalog;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>A statement's registered index, live tree, and bound key ordinals.</summary>
[global::System.Diagnostics.DebuggerDisplay("SqlLiveIndex")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Internal.SqlLiveIndex.DebugView))]
internal readonly record struct SqlLiveIndex(SqlCatalogIndex Metadata, BTreeIndex Index, int[] KeyOrdinals)
{
    internal BTreeRecordVersionIndex Versions { get; } = BTreeRecordVersionIndex.Create(Index);

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Sql.Internal.SqlLiveIndex _target;

        public DebugView(global::Assimalign.Cohesion.Database.Sql.Internal.SqlLiveIndex value)
        {
            _target = value;
        }

        public global::Assimalign.Cohesion.Database.Sql.Catalog.SqlCatalogIndex Metadata => _target.Metadata;
        public global::Assimalign.Cohesion.Database.Indexing.BTreeIndex Index => _target.Index;
        public int[] KeyOrdinals => _target.KeyOrdinals;
    }
}
