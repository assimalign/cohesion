using Assimalign.Cohesion.Database.Indexing;
using Assimalign.Cohesion.Database.Sql.Catalog;

namespace Assimalign.Cohesion.Database.Sql.Internal;

// One statement's choice of index and equality-prefix mapping for a bound key.
internal sealed record SqlConstraintLookup(SqlCatalogIndex? Metadata, BTreeIndex? Index,
    int[] PrefixKeyOrdinals, int[] IndexOrdinals)
{
    internal string AccessPath { get; } = Metadata is null ? "constraint-scan" : $"constraint-seek:{Metadata.Name}";
}
