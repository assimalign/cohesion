using System.Collections.Generic;

using Assimalign.Cohesion.Database.Sql.Catalog;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// A correlated equality-prefix seek into one input, with probe ordinals in
/// the accumulated physical row and the inner columns of the seek prefix, resolved
/// once when the statement is planned rather than by name for every outer row.
/// A null path means a buffered nested-loop scan.
/// </summary>
[global::System.Diagnostics.DebuggerDisplay("SqlJoinIndexPath")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Internal.SqlJoinIndexPath.DebugView))]
internal sealed record SqlJoinIndexPath(
    int InnerBinding,
    SqlCatalogIndex Index,
    IReadOnlyList<int> OuterOrdinals,
    IReadOnlyList<SqlCatalogColumn> InnerColumns)
{
    internal IReadOnlyList<SqlJoinIndexPath>? Alternatives { get; set; }

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Sql.Internal.SqlJoinIndexPath _target;

        public DebugView(global::Assimalign.Cohesion.Database.Sql.Internal.SqlJoinIndexPath value)
        {
            _target = value;
        }

        public int InnerBinding => _target.InnerBinding;
        public global::Assimalign.Cohesion.Database.Sql.Catalog.SqlCatalogIndex Index => _target.Index;
        public global::System.Collections.Generic.IReadOnlyList<int> OuterOrdinals => _target.OuterOrdinals;
        public global::System.Collections.Generic.IReadOnlyList<global::Assimalign.Cohesion.Database.Sql.Catalog.SqlCatalogColumn> InnerColumns => _target.InnerColumns;
    }
}
