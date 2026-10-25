using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>One projected output column of a SELECT.</summary>
/// <param name="Name">The output column name (alias, column name, or a synthesized name).</param>
/// <param name="ColumnOrdinal">The source column ordinal for pass-through projections; null for computed ones.</param>
/// <param name="Expression">The computed expression; null for pass-through projections. Planning reads it for types and collations.</param>
/// <param name="Type">The declared output type (<see cref="DatabaseType.Null"/> when not statically known).</param>
[global::System.Diagnostics.DebuggerDisplay("SqlProjection {Name,nq}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Internal.SqlProjection.DebugView))]
internal sealed record SqlProjection(string Name, int? ColumnOrdinal, SqlExpression? Expression, DatabaseType Type)
{
    /// <summary>Gets the bound computed expression the executor evaluates; null for pass-through projections.</summary>
    internal SqlBoundExpression? Value { get; init; }

    internal int? MaxLength { get; init; }

    /// <summary>Gets the collation <c>DISTINCT</c> compares and hashes the output's values under.</summary>
    internal SqlBoundCollation Collation { get; init; }

    /// <summary>Gets provenance needed only while binding a derived output; absent on ordinary stored queries.</summary>
    internal SqlProjectionMetadata? Metadata { get; init; }

    /// <summary>
    /// Gets whether a grouped output may be NULL: false only for a bare call of an aggregate that
    /// never returns NULL, such as <c>COUNT</c>, which counts 0 for an empty group.
    /// </summary>
    internal bool IsNullable { get; init; } = true;

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Sql.Internal.SqlProjection _target;

        public DebugView(global::Assimalign.Cohesion.Database.Sql.Internal.SqlProjection value)
        {
            _target = value;
        }

        public string Name => _target.Name;
        public int? ColumnOrdinal => _target.ColumnOrdinal;
        public global::Assimalign.Cohesion.Database.Sql.Language.SqlExpression? Expression => _target.Expression;
        public global::Assimalign.Cohesion.Database.Types.DatabaseType Type => _target.Type;
    }
}
