using System.Collections.Generic;

using Assimalign.Cohesion.Database.Sql.Catalog;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// An index seek: an equality prefix over the index's leading key columns
/// (plan-time-evaluated, storage-coerced values) plus an optional range bound on
/// the next key column. The full WHERE stays the residual predicate — a seek only
/// narrows the candidate set, so re-evaluating everything keeps seek results
/// exactly equivalent to the scan they replace.
/// </summary>
/// <param name="Index">The chosen index's catalog description.</param>
/// <param name="EqualityValues">The equality prefix values, one per leading key column, coerced to storage types.</param>
/// <param name="Lower">The optional lower bound on the key column after the prefix.</param>
/// <param name="Upper">The optional upper bound on the key column after the prefix.</param>
[global::System.Diagnostics.DebuggerDisplay("SqlIndexSeekPath")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Internal.SqlIndexSeekPath.DebugView))]
internal sealed record SqlIndexSeekPath(
    SqlCatalogIndex Index,
    IReadOnlyList<object?> EqualityValues,
    SqlSeekBound? Lower,
    SqlSeekBound? Upper) : SqlAccessPath
{
    // One optional reference; only competing paths allocate a cost description.
    internal SqlAccessCost? Cost { get; set; }

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Sql.Internal.SqlIndexSeekPath _target;

        public DebugView(global::Assimalign.Cohesion.Database.Sql.Internal.SqlIndexSeekPath value)
        {
            _target = value;
        }

        public global::Assimalign.Cohesion.Database.Sql.Catalog.SqlCatalogIndex Index => _target.Index;
        public global::System.Collections.Generic.IReadOnlyList<object?> EqualityValues => _target.EqualityValues;
        public global::Assimalign.Cohesion.Database.Sql.Internal.SqlSeekBound? Lower => _target.Lower;
        public global::Assimalign.Cohesion.Database.Sql.Internal.SqlSeekBound? Upper => _target.Upper;
    }
}
