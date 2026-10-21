using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>A key projection bound to exactly one immutable table version.</summary>
internal sealed class SqlBoundConstraintKey
{
    internal SqlBoundConstraintKey(SqlCatalogTable table, int[] ordinals, Collation[] collations)
    {
        Table = table;
        Ordinals = ordinals;
        Collations = collations;
    }

    internal SqlCatalogTable Table { get; }
    internal int[] Ordinals { get; }
    internal Collation[] Collations { get; }

    internal object?[] Read(object?[] row)
    {
        var key = new object?[Ordinals.Length];
        for (int index = 0; index < key.Length; index++) { key[index] = row[Ordinals[index]]; }
        return key;
    }
}
