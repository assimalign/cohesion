using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Assimalign.Cohesion.Database.Sql.Internal;

// Registries are allocated only after a spill; ordinary session/engine layouts stay unchanged.
[System.Diagnostics.DebuggerDisplay("SQL spill ownership")]
[System.Diagnostics.DebuggerTypeProxy(typeof(SqlSpillLifetime.DebugView))]
internal sealed class SqlSpillLifetime
{
    private static ConditionalWeakTable<SqlDatabaseSession, SqlSpillLifetime>? _sessions;
    private static ConditionalWeakTable<SqlDatabaseEngine, SqlSpillLifetime>? _engines;
    private readonly HashSet<SqlSpilledResultSet> _results = [];
    private bool _closed;
    internal const int MaximumResultsPerSession = 8;
    internal static void CheckCapacity(SqlDatabaseSession owner)
    {
        if (_sessions?.TryGetValue(owner, out var session) == true)
        { lock (session._results) { session.CheckCapacity(); } }
    }
    private void CheckCapacity()
    { if (_results.Count >= MaximumResultsPerSession) { throw SqlExecutionException.UndisposedResults(MaximumResultsPerSession); } }

    internal static void Attach(SqlDatabaseSession owner, SqlSpilledResultSet result)
    {
        LazyInitializer.EnsureInitialized(ref _sessions).GetValue(owner, _ => new()).Add(result, session: true);
        try { LazyInitializer.EnsureInitialized(ref _engines).GetValue(owner.Database.Engine, _ => new()).Add(result); result.CheckOwner(); }
        catch { Detach(owner, result); throw; }
    }
    private void Add(SqlSpilledResultSet result, bool session = false)
    {
        lock (_results) { ObjectDisposedException.ThrowIf(_closed, this); if (session) { CheckCapacity(); } _results.Add(result); }
    }
    internal static void Detach(SqlDatabaseSession owner, SqlSpilledResultSet result)
    {
        if (_sessions?.TryGetValue(owner, out var session) == true) { lock (session._results) { session._results.Remove(result); } }
        if (_engines?.TryGetValue(owner.Database.Engine, out var engine) == true) { lock (engine._results) { engine._results.Remove(result); } }
    }
    internal static void Close(SqlDatabaseSession owner) { if (_sessions?.TryGetValue(owner, out var value) == true) { value.Close(); } }
    internal static void Close(SqlDatabaseEngine owner) { if (_engines?.TryGetValue(owner, out var value) == true) { value.Close(); } }
    private void Close()
    {
        SqlSpilledResultSet[] results;
        // Registry locks never enter results or storage. Result gate -> run -> probe -> space;
        // disposal snapshots here and releases the registry before entering any result gate.
        lock (_results) { _closed = true; results = [.. _results]; _results.Clear(); }
        List<Exception>? failures = null;
        foreach (var result in results)
        {
            try { result.Dispose(); }
            catch (Exception exception) when (exception is not OutOfMemoryException) { (failures ??= []).Add(exception); }
        }
        if (failures is not null) { throw new AggregateException(ErrorMessages.SpillCleanupFailed, failures); }
    }
    private sealed class DebugView { public DebugView(SqlSpillLifetime target) { } }
}
