using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlPlanExecutor
{
    internal static Action? BeforeSequenceDdlLock { get; set; }
    internal static Action? BeforeSequenceCreate { get; set; }
    internal static Action? BeforeSequenceMetadataLease { get; set; }

    private async Task<DatabaseResult> ExecuteSequenceDdlAsync(SqlSequencePlan plan, SqlStatementContext statement, CancellationToken cancellationToken)
    {
        if (statement.ScriptReceipt is not null)
        { throw new SqlPlanningException("CS03074", ErrorMessages.SequencePersistedUnsupported); }
        if (plan.Expression is SqlCreateSequenceExpression create)
        {
            string schema = create.Sequence.SchemaName ?? SqlPlanner.DefaultSchema;
            BeforeSequenceCreate?.Invoke();
            // Linearize a guarded occupied-name no-op before validating ownership/options.
            if (create.IfNotExists && _catalog.RelationNameExists(schema, create.Sequence.TableName))
            { return new SqlQueryResult(DatabaseResultStatus.Success, 0); }
            await LockOwnershipTableAsync(create.Options, statement, cancellationToken).ConfigureAwait(false);
            var created = await _catalog.CreateSequenceIfAbsentAsync(schema, create.Sequence.TableName,
                () => SequenceOptions(create.Options, null), create.IfNotExists, cancellationToken, () => SequenceOwnership(create.Options, schema)).ConfigureAwait(false);
            if (created is not null) { statement.Metrics.RecordSelfCommit(); AfterSelfCommit?.Invoke(); }
            return new SqlQueryResult(DatabaseResultStatus.Success, 0);
        }
        var names = plan.Expression is SqlAlterSequenceExpression alter ? new[] { alter.Sequence }
            : ((SqlDropSequenceExpression)plan.Expression).Sequences;
        bool ifExists = plan.Expression is SqlAlterSequenceExpression change ? change.IfExists : ((SqlDropSequenceExpression)plan.Expression).IfExists;
        var expected = names.Select(name => _catalog.FindSequence(name.SchemaName ?? SqlPlanner.DefaultSchema, name.TableName)).ToArray();
        if (!ifExists && expected.Any(sequence => sequence is null)) { throw new SqlPlanningException("CS03073", ErrorMessages.SequenceMissing); }
        foreach (var sequence in expected.Where(s => s is not null).DistinctBy(s => s!.ObjectId).OrderBy(s => s!.ObjectId))
        {
            BeforeSequenceDdlLock?.Invoke();
            await statement.Coordinator.LockManager.AcquireAsync(statement.DdlLockOwner, LockResource.Object(sequence!.ObjectId), LockMode.Exclusive, cancellationToken).ConfigureAwait(false);
            SqlSequenceExecution.CheckGrant(statement, statement.DdlLockOwner);
        }
        if (plan.Expression is SqlAlterSequenceExpression ownerChange && expected[0] is { } ownerSequence &&
            _catalog.FindSequence(ownerSequence.ObjectId) is not null)
        { await LockOwnershipTableAsync(ownerChange.Options, statement, cancellationToken).ConfigureAwait(false); }
        if (plan.Expression is SqlDropSequenceExpression { Cascade: true })
        {
            var dependencies = expected.Where(sequence => sequence is not null).Select(sequence => SqlSequenceDefault.Dependency(sequence!.ObjectId)).ToHashSet(StringComparer.Ordinal);
            // Sequence X prevents new default dependencies; table X serializes removal
            // against unrelated column DDL so stale replacements cannot restore one.
            foreach (var table in _catalog.Tables.Where(table => table.Columns.Any(column => column.DefaultFunctionCalls.Any(dependencies.Contains))).OrderBy(table => table.ObjectId))
            {
                await statement.Coordinator.LockManager.AcquireAsync(statement.DdlLockOwner,
                    LockResource.Object(table.ObjectId), LockMode.Exclusive, cancellationToken).ConfigureAwait(false);
                SqlSequenceExecution.CheckGrant(statement, statement.DdlLockOwner);
                var current = ReadCurrentTable(table);
                EnsureSameIdentity(table, current);
                EnsureCanChange(current.Owner, current.Name, current.OwningSchema, "DROP SEQUENCE CASCADE", statement);
            }
        }
        BeforeSequenceMetadataLease?.Invoke();
        using var metadata = statement.DdlInterlock is { } interlock
            ? await interlock.EnterMetadataAsync(definition: false, cancellationToken, statement.MetadataLockTimeout).ConfigureAwait(false) : null;
        SqlSequenceExecution.CheckGrant(statement, statement.DdlLockOwner);
        var resolved = new List<SqlCatalogSequence>();
        for (int i = 0; i < names.Count; i++)
        {
            if (expected[i] is null && ifExists) { continue; }
            var sequence = _catalog.FindSequence(names[i].SchemaName ?? SqlPlanner.DefaultSchema, names[i].TableName);
            if (sequence is null && ifExists) { continue; }
            if (sequence?.ObjectId != expected[i]?.ObjectId) { throw new SqlPlanningException("CS04067", ErrorMessages.SequenceChanged); }
            if (sequence is not null) { resolved.Add(sequence); }
        }
        if (plan.Expression is SqlAlterSequenceExpression alteration && resolved.Count > 0)
        {
            if (resolved[0].IsIdentity && _catalog.TryGetTable(resolved[0].Schema,
                _catalog.Tables.First(table => table.ObjectId == resolved[0].OwnerTableId).Name, out var ownerTable))
            { EnsureCanChange(ownerTable.Owner, ownerTable.Name, ownerTable.OwningSchema, "ALTER SEQUENCE", statement); }
            // PostgreSQL sequence.c:1254-1255 keeps OWNED BY-only changes off the allocator state.
            var options = alteration.Options.All(option => option.Name == "OWNED") ? resolved[0].Options : SequenceOptions(alteration.Options, resolved[0].Options);
            if (resolved[0].IsIdentity && alteration.Options.Any(option => option.Name == "OWNED")) { throw new SqlPlanningException("CS03081", ErrorMessages.SequenceOwnershipInvalid); }
            var restart = alteration.Options.FirstOrDefault(option => option.Name == "RESTART");
            await SelfCommitAsync(statement, _catalog.AlterSequenceAsync(resolved[0], options,
                restart is null ? null : restart.Value ?? options.Start, cancellationToken,
                alteration.Options.Any(option => option.Name == "OWNED") ? SequenceOwnership(alteration.Options, resolved[0].Schema) : null)).ConfigureAwait(false);
        }
        else if (plan.Expression is SqlDropSequenceExpression drop && resolved.Count > 0)
        {
            await SelfCommitAsync(statement, _catalog.DropSequencesAsync(resolved.DistinctBy(s => s.ObjectId).ToArray(), cancellationToken, drop.Cascade)).ConfigureAwait(false);
        }
        return new SqlQueryResult(DatabaseResultStatus.Success, 0);
    }

    private async ValueTask LockOwnershipTableAsync(IReadOnlyList<SqlSequenceOption> options, SqlStatementContext statement, CancellationToken cancellationToken)
    {
        var owner = options.FirstOrDefault(option => option.Name == "OWNED")?.OwnerTable;
        if (owner is not null)
        {
            if (!_catalog.TryGetTable(owner.SchemaName ?? SqlPlanner.DefaultSchema, owner.TableName, out var table))
            { throw new SqlPlanningException("CS03081", ErrorMessages.SequenceOwnershipInvalid); }
            // Existing sequence X was taken before this table X. CREATE has no published sequence yet.
            await statement.Coordinator.LockManager.AcquireAsync(statement.DdlLockOwner,
                LockResource.Object(table.ObjectId), LockMode.Exclusive, cancellationToken).ConfigureAwait(false);
            SqlSequenceExecution.CheckGrant(statement, statement.DdlLockOwner);
            var current = ReadCurrentTable(table);
            EnsureSameIdentity(table, current);
            EnsureCanChange(current.Owner, current.Name, current.OwningSchema, "OWNED BY", statement);
        }
    }

    private (ulong Table, string? Column) SequenceOwnership(IReadOnlyList<SqlSequenceOption> options, string schema)
    {
        var owner = options.FirstOrDefault(option => option.Name == "OWNED");
        if (owner?.OwnerTable is not { } reference) { return default; }
        if (!schema.Equals(reference.SchemaName ?? SqlPlanner.DefaultSchema, StringComparison.OrdinalIgnoreCase) ||
            !_catalog.TryGetTable(schema, reference.TableName, out var table) || table.FindColumn(owner.OwnerColumn!) is not { } column)
        { throw new SqlPlanningException("CS03081", ErrorMessages.SequenceOwnershipInvalid); }
        return (table.ObjectId, column.Name);
    }

    internal static SqlSequenceOptions SequenceOptions(IReadOnlyList<SqlSequenceOption> options, SqlSequenceOptions? previous, DatabaseType? defaultType = null, long defaultCache = 1)
    {
        SqlSequenceOption? Find(string name) => options.FirstOrDefault(option => option.Name == name);
        var typeOption = Find("AS");
        DatabaseType type = typeOption is null ? previous?.Type ?? defaultType ?? DatabaseType.Int64 : typeOption.TypeName switch
        {
            "SMALLINT" or "INT2" => DatabaseType.Int16,
            "INT" or "INTEGER" or "INT4" => DatabaseType.Int32,
            "BIGINT" or "INT8" => DatabaseType.Int64,
            _ => throw new SqlCatalogException("CS03069", ErrorMessages.SequenceTypeInvalid),
        };
        long increment = Find("INCREMENT")?.Value ?? previous?.Increment ?? 1;
        long? minimum = Find("MINVALUE") is { } min ? min.Value : previous?.Minimum;
        long? maximum = Find("MAXVALUE") is { } max ? max.Value : previous?.Maximum;
        // PostgreSQL sequence.c:1355-1401: AS changes a former type-limit bound to the new type limit.
        if (previous is not null && type != previous.Type)
        {
            var oldBounds = SqlSequenceOptions.IntegerBounds(previous.Type);
            var newBounds = SqlSequenceOptions.IntegerBounds(type);
            if (Find("MINVALUE") is null && previous.Minimum == oldBounds.Minimum) { minimum = newBounds.Minimum; }
            if (Find("MAXVALUE") is null && previous.Maximum == oldBounds.Maximum) { maximum = newBounds.Maximum; }
        }
        return new SqlSequenceOptions(type, Find("START")?.Value ?? previous?.Start, increment, minimum, maximum,
            Find("CACHE")?.Value ?? previous?.Cache ?? defaultCache, Find("CYCLE") is { } cycle ? cycle.Value == 1 : previous?.Cycle ?? false, identity: type == DatabaseType.Int8);
    }
}
