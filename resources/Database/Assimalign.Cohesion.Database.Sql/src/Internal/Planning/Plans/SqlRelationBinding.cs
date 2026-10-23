using System.Collections.Generic;

using Assimalign.Cohesion.Database.Sql.Language;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>A relation's source, qualifier and position in a joined row.</summary>
[global::System.Diagnostics.DebuggerDisplay("SqlRelationBinding")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Internal.SqlRelationBinding.DebugView))]
internal sealed record SqlRelationBinding(SqlRelationSource Source, SqlRelationReference Reference, int Offset)
{
    // USING hides only unqualified right columns, never their qualified references.
    internal HashSet<int> HiddenColumns { get; } = [];

    /// <summary>Merged USING expressions keyed by their preserved physical column ordinal.</summary>
    internal Dictionary<int, SqlUsingColumn>? UsingColumns { get; set; }

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Sql.Internal.SqlRelationBinding _target;

        public DebugView(global::Assimalign.Cohesion.Database.Sql.Internal.SqlRelationBinding value)
        {
            _target = value;
        }

        public global::Assimalign.Cohesion.Database.Sql.Internal.SqlRelationSource Source => _target.Source;
        public global::Assimalign.Cohesion.Database.Sql.Language.SqlRelationReference Reference => _target.Reference;
        public int Offset => _target.Offset;
    }
}
