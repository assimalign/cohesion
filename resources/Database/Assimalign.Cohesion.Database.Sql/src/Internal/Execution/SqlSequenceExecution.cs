using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Sql.Internal;

[global::System.Diagnostics.DebuggerDisplay("SqlSequenceExecution")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed class SqlSequenceExecution : IDisposable
{
    private static readonly AsyncLocal<SqlSequenceExecution?> _current = new();
    private static readonly string _transactionEnded = ErrorMessages.SequenceTransactionEnded;
    private readonly SqlCatalog _catalog;
    private readonly SqlSequenceSessionState _session;
    private readonly IReadOnlyList<SqlCatalogSequence> _sequences;
    private readonly SqlSequenceExecution? _previous;
    private Dictionary<ulong, long>? _rowValues;
    private int _rowDepth;
    private object? _lastName;
    private SqlCatalogSequence? _lastResolved;
    private readonly CancellationToken _cancellationToken;

    private SqlSequenceExecution(SqlCatalog catalog, SqlSequenceSessionState session,
        IReadOnlyList<SqlCatalogSequence> sequences, CancellationToken cancellationToken)
    { _catalog = catalog; _session = session; _sequences = sequences; _previous = Current; _cancellationToken = cancellationToken; }

    internal static SqlSequenceExecution? Current => _current.Value;
    internal static Action<TransactionContext>? AfterSequenceGrant { get; set; }

    internal static void RequireDefaultGrants(SqlCatalogColumn column)
    {
        var execution = Current;
        if (column.Identity is not null && execution is null)
        { throw new SqlPlanningException("CS04067", ErrorMessages.SequenceChanged); }
        for (int index = 0; index < column.DefaultFunctionCalls.Count; index++)
        {
            string dependency = column.DefaultFunctionCalls[index];
            if (dependency.StartsWith("S:", StringComparison.Ordinal) && ulong.TryParse(dependency.AsSpan(2), out ulong id) &&
                (execution is null || !execution.HasGrant(id)))
            { throw new SqlPlanningException("CS04067", ErrorMessages.SequenceChanged); }
        }
    }

    private bool HasGrant(ulong id)
    {
        for (int index = 0; index < _sequences.Count; index++)
        { if (_sequences[index].ObjectId == id) { return true; } }
        return false;
    }

    internal static void CheckGrant(SqlStatementContext statement, TransactionSequence owner)
    {
        AfterSequenceGrant?.Invoke(statement.Transaction);
        if (statement.Transaction.State != TransactionState.Active)
        {
            statement.Coordinator.LockManager.ReleaseAll(owner);
            throw new TransactionAbortedException(_transactionEnded);
        }
    }

    // Lock/latch order: transaction-owned sequence object locks (ascending ObjectId), then
    // table/row locks, then the data apply gate, then catalog _sync and storage latches.
    // No catalog monitor or definition lease spans a lock wait. Non-definition table/CTAS
    // leases remain compatible with sequence DDL's non-definition lease. Constant references lock only
    // their objects; dynamic name expressions conservatively lock the captured directory.
    // DML uses the transaction owner; provisioning uses its retained DDL owner, matching
    // table grants and avoiding self-waits against its pre-acquired sequence X grants.
    // Grants belong to that owner, including failed statements, cancellation and disposal;
    // the session/coordinator's existing commit/rollback/timeout cleanup releases them.
    internal static async ValueTask<SqlSequenceExecution> OpenAsync(SqlCatalog catalog, SqlStatementContext statement,
        SqlSequenceSessionState session, CancellationToken cancellationToken, SqlSequenceUsage? usage = null)
    {
        // Identity/default binding pins an object id. One immutable incarnation needs no
        // temporary directory, LINQ ordering buffers or copied singleton collection.
        if (usage is { DynamicNames: false } && usage.TrySingleIdentity(out ulong id))
        {
            var sequence = catalog.FindSequence(id);
            if (sequence is not null)
            {
                await statement.Coordinator.LockManager.AcquireAsync(statement.DdlLockOwner,
                    LockResource.Object(id), LockMode.Shared, cancellationToken).ConfigureAwait(false);
                CheckGrant(statement, statement.DdlLockOwner);
                sequence = catalog.FindSequence(id);
            }
            return new SqlSequenceExecution(catalog, session, sequence?.Singleton ?? Array.Empty<SqlCatalogSequence>(), cancellationToken);
        }
        var captured = (usage is null || usage.DynamicNames ? catalog.Sequences : ResolveNames(catalog, session, usage))
            .DistinctBy(sequence => sequence.ObjectId).OrderBy(sequence => sequence.ObjectId).ToArray();
        var current = new List<SqlCatalogSequence>(captured.Length);
        foreach (var sequence in captured)
        {
            await statement.Coordinator.LockManager.AcquireAsync(statement.DdlLockOwner,
                LockResource.Object(sequence.ObjectId), LockMode.Shared, cancellationToken).ConfigureAwait(false);
            CheckGrant(statement, statement.DdlLockOwner);
            // ALTER may have committed while this request waited: bind its new options under our lock.
            var resolved = catalog.FindSequence(sequence.ObjectId);
            if (resolved?.ObjectId == sequence.ObjectId) { current.Add(resolved); }
        }
        return new SqlSequenceExecution(catalog, session, current, cancellationToken);
    }

    private static IReadOnlyList<SqlCatalogSequence> ResolveNames(SqlCatalog catalog, SqlSequenceSessionState session, SqlSequenceUsage usage)
    {
        var resolved = new List<SqlCatalogSequence>();
        foreach (object? name in usage.Names)
        {
            SqlCatalogSequence? sequence = null;
            if (ReferenceEquals(name, SqlSequenceUsage.LastValue)) { sequence = catalog.FindSequence(session.Last); }
            else if (name is sbyte or byte or short or ushort or int or uint or long or ulong)
            {
                ulong id = name is ulong unsigned ? unsigned : unchecked((ulong)Convert.ToInt64(name, CultureInfo.InvariantCulture));
                sequence = catalog.FindSequence(id);
            }
            else if (name is string text && TryName(text, out string schema, out string identifier)) { sequence = catalog.FindSequence(schema, identifier); }
            if (sequence is not null) { resolved.Add(sequence); }
        }
        return resolved;
    }

    // AsyncLocal changes made inside OpenAsync do not flow back to its caller.
    internal void Enter() => _current.Value = this;
    public void Dispose() => _current.Value = _previous;
    internal IReadOnlyDictionary<ulong, long>? CaptureRow() => _rowValues is not { Count: > 0 } ? null : new Dictionary<ulong, long>(_rowValues);

    internal RowScope BeginRow(IReadOnlyDictionary<ulong, long>? seed = null)
    {
        var previous = _rowValues;
        if (_rowDepth++ == 0) { _rowValues?.Clear(); }
        else { _rowValues = null; }
        if (seed is not null)
        { _rowValues ??= new(); foreach (var pair in seed) { _rowValues[pair.Key] = pair.Value; } }
        return new RowScope(this, previous);
    }

    [global::System.Diagnostics.DebuggerDisplay("Sequence row scope")]
    [global::System.Diagnostics.DebuggerTypeProxy(typeof(RowScope.DebugView))]
    internal readonly struct RowScope : IDisposable
    {
        private readonly SqlSequenceExecution _execution;
        private readonly Dictionary<ulong, long>? _previous;
        internal RowScope(SqlSequenceExecution execution, Dictionary<ulong, long>? previous)
        { _execution = execution; _previous = previous; }
        public void Dispose()
        {
            if (_execution._rowDepth > 1) { _execution._rowValues = _previous; }
            _execution._rowDepth--;
        }
        private sealed class DebugView { public DebugView(RowScope scope) { _ = scope; } }
    }

    internal object? Evaluate(string operation, object? name, object? value, object? isCalled, object?[] row)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        if (name is SqlCharacter character) { name = character.Text; }
        if (operation == "LASTVAL")
        {
            if (_session.Last == 0 || !_sequences.Any(s => s.ObjectId == _session.Last))
            { throw new SqlPlanningException("CS04069", ErrorMessages.SequenceLastUndefined); }
            return _session.Values[_session.Last];
        }
        if (name is null || operation == "SETVAL" && (value is null || isCalled is null)) { return null; }
        var sequence = Resolve(name);
        if (operation == "REGCLASS") { return checked((long)sequence.ObjectId); }
        if (operation == "CURRVAL")
        {
            if (!_session.Values.TryGetValue(sequence.ObjectId, out long last))
            { throw new SqlPlanningException("CS04068", ErrorMessages.SequenceCurrentUndefined); }
            return last;
        }
        if (operation == "SETVAL")
        {
            if (value is not (sbyte or short or int or long) || isCalled is not bool called)
            { throw new SqlPlanningException("CS03077", ErrorMessages.SequenceArgumentsInvalid); }
            long number = Convert.ToInt64(value, CultureInfo.InvariantCulture);
            _catalog.SetSequence(sequence, number, called);
            if (called) { _session.Values[sequence.ObjectId] = number; }
            return number;
        }
        if (operation == "NEXT VALUE FOR")
        {
            if (_rowValues?.TryGetValue(sequence.ObjectId, out long existing) == true) { return existing; }
        }
        long next = _catalog.AllocateSequence(sequence.ObjectId);
        _session.Values[sequence.ObjectId] = next;
        _session.Last = sequence.ObjectId;
        if (operation == "NEXT VALUE FOR") { (_rowValues ??= new())[sequence.ObjectId] = next; }
        return next;
    }

    internal long AllocatePreparedIdentity(SqlCatalogSequence sequence)
    {
        long value = _catalog.AllocateSequence(sequence);
        _session.Values[sequence.ObjectId] = value; _session.Last = sequence.ObjectId;
        return value;
    }

    internal long AllocateIdentity(ulong tableId, string column)
    {
        SqlCatalogSequence? sequence = null;
        for (int i = 0; i < _sequences.Count; i++)
        { var candidate = _sequences[i]; if (candidate.IsIdentity && candidate.OwnerTableId == tableId && string.Equals(candidate.OwnerColumn, column, StringComparison.OrdinalIgnoreCase)) { sequence = candidate; break; } }
        if (sequence is null) { throw new SqlPlanningException("CS04067", ErrorMessages.SequenceChanged); }
        long value = _catalog.AllocateSequence(sequence.ObjectId);
        _session.Values[sequence.ObjectId] = value;
        _session.Last = sequence.ObjectId;
        return value;
    }

    private SqlCatalogSequence Resolve(object name)
    {
        if (Equals(name, _lastName) && _lastResolved is { } cached) { return cached; }
        return Remember(name, ResolveCore(name));
    }

    private SqlCatalogSequence Remember(object name, SqlCatalogSequence sequence)
    { _lastName = name; _lastResolved = sequence; return sequence; }

    private SqlCatalogSequence ResolveCore(object name)
    {
        if (name is sbyte or byte or short or ushort or int or uint or long or ulong)
        {
            ulong id = name is ulong unsigned ? unsigned : unchecked((ulong)Convert.ToInt64(name, CultureInfo.InvariantCulture));
            return _sequences.FirstOrDefault(s => s.ObjectId == id) ?? throw new SqlPlanningException("CS03073", ErrorMessages.SequenceMissing);
        }
        if (name is not string text || !TryName(text, out string schema, out string identifier))
        { throw new SqlPlanningException("CS03077", ErrorMessages.SequenceArgumentsInvalid); }
        return _sequences.FirstOrDefault(s => s.Schema.Equals(schema, StringComparison.OrdinalIgnoreCase) && s.Name.Equals(identifier, StringComparison.OrdinalIgnoreCase))
            ?? throw new SqlPlanningException("CS03073", ErrorMessages.SequenceMissing);
    }

    internal static bool TryName(string text, out string schema, out string name)
    {
        var parts = new List<string>(2);
        int index = 0;
        while (index < text.Length)
        {
            while (index < text.Length && char.IsWhiteSpace(text[index])) { index++; }
            var part = new StringBuilder();
            if (index < text.Length && text[index] == '"')
            {
                index++; bool closed = false;
                while (index < text.Length)
                {
                    char c = text[index++];
                    if (c != '"') { part.Append(c); continue; }
                    if (index < text.Length && text[index] == '"') { index++; part.Append('"'); }
                    else { closed = true; break; }
                }
                if (!closed) { break; }
            }
            else
            {
                while (index < text.Length && (char.IsLetterOrDigit(text[index]) || text[index] is '_' or '$')) { part.Append(text[index++]); }
            }
            if (part.Length == 0) { break; }
            parts.Add(part.ToString());
            while (index < text.Length && char.IsWhiteSpace(text[index])) { index++; }
            if (index == text.Length && parts.Count <= 2)
            { schema = parts.Count == 1 ? SqlPlanner.DefaultSchema : parts[0]; name = parts[^1]; return true; }
            if (index == text.Length || text[index++] != '.' || parts.Count > 1) { break; }
        }
        schema = name = ""; return false;
    }

    internal static string Name(SqlTableReference name)
        => "\"" + (name.SchemaName ?? SqlPlanner.DefaultSchema).Replace("\"", "\"\"", StringComparison.Ordinal) + "\".\"" + name.TableName.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    internal static bool IsFunction(string name) => name.ToUpperInvariant() is "NEXTVAL" or "CURRVAL" or "SETVAL" or "LASTVAL";
    internal static void CheckCall(SqlFunctionCallExpression call)
    {
        int count = call.Arguments.Count;
        bool valid = call.FunctionName.ToUpperInvariant() switch
        { "LASTVAL" => count == 0, "SETVAL" => count is 2 or 3, _ => count == 1 };
        if (!valid || call.Arguments.Any(a => a is SqlStarExpression) || call.Quantifier != SqlSetQuantifier.Default || call.OrderBy.Count != 0 || call.Filter is not null)
        { throw SqlEvaluationException.FunctionSignatureMismatch(call.FunctionName, "sequence arguments", count.ToString(CultureInfo.InvariantCulture), "nextval(name), currval(name), setval(name, integer [, boolean]), lastval()"); }
    }

    internal static Action? PersistedNodeVisited { get; set; }

    internal static void RefusePersistedNode(SqlExpression expression)
    {
        PersistedNodeVisited?.Invoke();
        if (expression is SqlCastExpression cast && cast.TargetType.Equals("REGCLASS", StringComparison.OrdinalIgnoreCase) || expression is SqlNextValueExpression || expression is SqlFunctionCallExpression call && IsFunction(call.FunctionName))
        { throw new SqlPlanningException("CS03074", ErrorMessages.SequencePersistedUnsupported, expression.Location); }
    }

    internal static void RefusePersisted(SqlExpression? expression)
    {
        if (expression is null) { return; }
        foreach (var node in SqlViewQuery.Nodes(expression)) { RefusePersistedNode(node); }
    }

    internal static void RefusePersistedStatement(SqlQueryExpression expression)
    {
        switch (expression)
        {
            case SqlCreateDomainExpression domain: RefusePersisted(domain.DefaultValue); RefusePersisted(domain.Check); break;
            case SqlCreateFunctionExpression function: RefusePersisted(function.Body); break;
            case SqlCreateViewExpression view:
                foreach (var node in SqlViewQuery.Nodes(view.Query)) { RefusePersistedNode(node); } break;
            case SqlCreateTableExpression create:
                foreach (var column in create.Columns) { SqlSequenceDefault.RefuseUnsupported(column.DefaultValue); foreach (var c in column.Constraints) { RefusePersisted(c.CheckExpression); } }
                foreach (var c in create.Constraints) { RefusePersisted(c.CheckExpression); }
                break;
            case SqlAlterTableExpression { Action: SqlAlterSetDefaultAction change }: SqlSequenceDefault.RefuseUnsupported(change.DefaultValue); break;
            case SqlAlterTableExpression { Action: SqlAlterAddColumnAction add }:
                SqlSequenceDefault.RefuseUnsupported(add.Column.DefaultValue); foreach (var c in add.Column.Constraints) { RefusePersisted(c.CheckExpression); } break;
            case SqlAlterTableExpression { Action: SqlAlterAddConstraintAction add }: RefusePersisted(add.Constraint.CheckExpression); break;
        }
    }

    private sealed class DebugView
    { public DebugView(SqlSequenceExecution execution) { _ = execution; } }
}
