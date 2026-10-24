using System.Runtime.CompilerServices;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// Four <see cref="SqlValue"/> slots on the stack: the arguments of one function call, so a call
/// of up to four arguments allocates nothing. A call with more rents a pooled array instead.
/// </summary>
[global::System.Diagnostics.DebuggerDisplay("SqlValueBuffer")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Internal.SqlValueBuffer.DebugView))]
[InlineArray(Length)]
internal struct SqlValueBuffer
{
    /// <summary>The number of slots.</summary>
    internal const int Length = 4;

    private SqlValue _element;

    private sealed class DebugView
    {
        public DebugView(global::Assimalign.Cohesion.Database.Sql.Internal.SqlValueBuffer value)
        {
            _ = value;
        }
    }
}
