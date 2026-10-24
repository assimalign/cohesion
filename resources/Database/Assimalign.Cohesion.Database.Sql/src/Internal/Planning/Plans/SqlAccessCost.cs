using System.Collections.Generic;
using Assimalign.Cohesion.Database.Indexing;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>Transient alternatives and bounded probe evidence; never persisted.</summary>
[global::System.Diagnostics.DebuggerDisplay("SqlAccessCost")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Internal.SqlAccessCost.DebugView))]
internal sealed class SqlAccessCost
{
    internal SqlAccessCost(IReadOnlyList<SqlIndexSeekPath> alternatives)
    {
        Alternatives = alternatives;
    }

    internal IReadOnlyList<SqlIndexSeekPath> Alternatives { get; }
    internal BTreeRangeCount? Estimate { get; set; }

    private sealed class DebugView
    {
        public DebugView(global::Assimalign.Cohesion.Database.Sql.Internal.SqlAccessCost value)
        {
            _ = value;
        }
    }
}
