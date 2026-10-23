using Assimalign.Cohesion.Database.Indexing;
using Assimalign.Cohesion.Database.Sql.Catalog;

namespace Assimalign.Cohesion.Database.Sql.Internal;

// One statement's choice of index and equality-prefix mapping for a bound key.
[global::System.Diagnostics.DebuggerDisplay("SqlConstraintLookup")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Internal.SqlConstraintLookup.DebugView))]
internal sealed record SqlConstraintLookup(SqlCatalogIndex? Metadata, BTreeIndex? Index,
    int[] PrefixKeyOrdinals, int[] IndexOrdinals)
{
    internal string AccessPath { get; } = Metadata is null ? "constraint-scan" : $"constraint-seek:{Metadata.Name}";

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Sql.Internal.SqlConstraintLookup _target;

        public DebugView(global::Assimalign.Cohesion.Database.Sql.Internal.SqlConstraintLookup value)
        {
            _target = value;
        }

        public global::Assimalign.Cohesion.Database.Sql.Catalog.SqlCatalogIndex? Metadata => _target.Metadata;
        public global::Assimalign.Cohesion.Database.Indexing.BTreeIndex? Index => _target.Index;
        public int[] PrefixKeyOrdinals => _target.PrefixKeyOrdinals;
        public int[] IndexOrdinals => _target.IndexOrdinals;
    }
}
