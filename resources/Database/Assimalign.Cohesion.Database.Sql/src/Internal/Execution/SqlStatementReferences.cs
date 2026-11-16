using System.Collections.Generic;

using Assimalign.Cohesion.Database.Sql.Catalog;

namespace Assimalign.Cohesion.Database.Sql.Internal;

// Shared by copies of the statement context. Definitions are read under object intent locks;
// this state never survives into the next statement, including in an explicit transaction.
[global::System.Diagnostics.DebuggerDisplay("SqlStatementReferences")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Internal.SqlStatementReferences.DebugView))]
internal sealed class SqlStatementReferences
{
    internal long ReferenceCatalogTableVisits;
    internal int IncomingReferenceBuilds;
    internal int ReferenceIntentLockBatches;
    internal int ConstraintLookupBuilds;
    internal int LiveIndexBuilds;

    private Dictionary<ulong, IReadOnlyList<SqlBoundReference>>? _incoming;
    internal Dictionary<ulong, IReadOnlyList<SqlBoundReference>> Incoming => _incoming ??= [];
    private Dictionary<SqlCatalogConstraint, SqlCatalogTable>? _parents;
    internal Dictionary<SqlCatalogConstraint, SqlCatalogTable> Parents => _parents ??= [];
    private Dictionary<SqlCatalogConstraint, SqlBoundReference>? _bindings;
    internal Dictionary<SqlCatalogConstraint, SqlBoundReference> Bindings => _bindings ??= [];
    private Dictionary<SqlBoundConstraintKey, SqlConstraintLookup>? _lookups;
    internal Dictionary<SqlBoundConstraintKey, SqlConstraintLookup> Lookups => _lookups ??= [];
    private Dictionary<SqlCatalogTable, List<SqlLiveIndex>>? _liveIndexes;
    internal Dictionary<SqlCatalogTable, List<SqlLiveIndex>> LiveIndexes => _liveIndexes ??= [];
    private HashSet<ulong>? _incomingLocks;
    internal HashSet<ulong> IncomingLocks => _incomingLocks ??= [];
    private HashSet<ulong>? _outgoingLocks;
    internal HashSet<ulong> OutgoingLocks => _outgoingLocks ??= [];

    // Every definition/lock-coverage claim was made while grants that may have just
    // been released were held. Rebuild all attempt-local caches after a wait.
    internal void ClearAfterCascadeRetry()
    {
        _incoming?.Clear(); _parents?.Clear(); _bindings?.Clear(); _lookups?.Clear();
        _liveIndexes?.Clear(); _incomingLocks?.Clear(); _outgoingLocks?.Clear();
    }

    private sealed class DebugView
    {
        public DebugView(global::Assimalign.Cohesion.Database.Sql.Internal.SqlStatementReferences value)
        {
            _ = value;
        }
    }
}
