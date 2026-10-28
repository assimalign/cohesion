using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Sql.Internal;

[global::System.Diagnostics.DebuggerDisplay("SqlSequenceSessionState")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed class SqlSequenceSessionState
{
    internal Dictionary<ulong, long> Values { get; } = new();
    internal ulong Last { get; set; }

    private sealed class DebugView
    {
        public DebugView(SqlSequenceSessionState state) { _ = state; }
    }
}
