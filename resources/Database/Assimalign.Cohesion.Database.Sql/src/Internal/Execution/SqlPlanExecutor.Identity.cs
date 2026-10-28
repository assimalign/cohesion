using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlPlanExecutor
{
    private void VerifyOwnedSequences(IEnumerable<SqlCatalogTable> tables, IReadOnlySet<ulong> grants, SqlStatementContext statement)
    {
        SqlSequenceExecution.CheckGrant(statement, statement.DdlLockOwner);
        if (!_catalog.HasSequences) { return; }
        var owners = tables.Select(table => table.ObjectId).ToHashSet();
        if (_catalog.Sequences.Any(sequence => owners.Contains(sequence.OwnerTableId) && !grants.Contains(sequence.ObjectId)))
        { throw new SqlPlanningException("CS04067", ErrorMessages.SequenceChanged); }
    }

    // Lock/latch order: owned sequence X grants by id, then table X, then apply
    // gate, catalog monitor and storage latches. No catalog monitor spans a wait.
    // Transaction/session cleanup owns every grant, including failures and cancellation.
    private async ValueTask<IReadOnlySet<ulong>> LockOwnedSequencesAsync(IEnumerable<SqlCatalogTable> tables, SqlStatementContext statement, CancellationToken cancellationToken)
    {
        if (!_catalog.HasSequences) { return System.Collections.Frozen.FrozenSet<ulong>.Empty; }
        var ids = tables.Select(table => table.ObjectId).ToHashSet();
        var grants = new HashSet<ulong>();
        foreach (var sequence in _catalog.Sequences.Where(sequence => ids.Contains(sequence.OwnerTableId)).OrderBy(sequence => sequence.ObjectId))
        {
            await statement.Coordinator.LockManager.AcquireAsync(statement.DdlLockOwner,
                LockResource.Object(sequence.ObjectId), LockMode.Exclusive, cancellationToken).ConfigureAwait(false);
            SqlSequenceExecution.CheckGrant(statement, statement.DdlLockOwner);
            grants.Add(sequence.ObjectId);
        }
        return grants;
    }

    private async Task<DatabaseResult> ExecuteAlterIdentityAsync(SqlAlterIdentityPlan plan, SqlStatementContext statement, CancellationToken cancellationToken)
    {
        await AcquireObjectLockAsync(statement, plan.Table.Schema, plan.Table.Name, "ALTER TABLE identity", cancellationToken).ConfigureAwait(false);
        EnsureCurrentDefinition(plan.Table);
        int ordinal = FindColumnOrdinal(plan.Table, plan.Action.ColumnName);
        var column = plan.Table.Columns[ordinal];
        var action = plan.Action;
        if (action.Operation == "ADD")
        {
            if (column.Identity is not null) { throw SqlPlanningException.IdentityViolation(column.Name, ErrorMessages.IdentityAlreadyDeclared); }
            if (column.IsNullable) { throw new SqlPlanningException("CS03078", ErrorMessages.IdentityNotNullRequired); }
            if (column.DefaultLiteral is not null) { throw SqlPlanningException.IdentityViolation(column.Name, ErrorMessages.IdentityDefaultConflict); }
            if (column.DomainName is not null || column.Type.Type is not (DatabaseType.Int8 or DatabaseType.Int16 or DatabaseType.Int32 or DatabaseType.Int64))
            { throw SqlPlanningException.IdentityViolation(column.Name, ErrorMessages.EngineIdentityIntegerRequired); }
            if (plan.Table.Columns.Any(candidate => candidate.Identity is not null))
            { throw SqlPlanningException.IdentityViolation(column.Name, ErrorMessages.IdentityAlreadyDeclared); }
        }
        else if (column.Identity is null)
        {
            if (action.Operation == "DROP" && action.IfExists) { return new SqlQueryResult(DatabaseResultStatus.Success, 0); }
            throw new SqlPlanningException("CS03079", ErrorMessages.IdentityRequired);
        }
        var identity = column.Identity;
        SqlSequenceOptions? options = null;
        long? restart = null;
        if (action.Operation == "ADD")
        {
            var declared = action.Identity!;
            options = SequenceOptions(declared.Options, null, column.Type.Type, defaultCache: 1);
            identity = new SqlCatalogIdentity(declared.IsAlways, options.Start, options.Increment) { Options = options };
        }
        else if (action.Operation == "DROP") { identity = null; }
        else if (action.Operation == "SET GENERATED")
        { identity = new SqlCatalogIdentity(action.IsAlways!.Value, identity!.Start, identity.Increment); }
        else
        {
            var sequence = _catalog.IdentitySequence(plan.Table.ObjectId, column.Name);
            options = SequenceOptions(action.Options, sequence.Options);
            var restartOption = action.Options.FirstOrDefault(option => option.Name == "RESTART");
            restart = restartOption is null ? null : restartOption.Value ?? options.Start;
            identity = new SqlCatalogIdentity(action.IsAlways ?? identity!.IsAlways, options.Start, options.Increment);
        }
        var columns = plan.Table.Columns.ToArray();
        columns[ordinal] = new SqlCatalogColumn(column.Name, column.Type, column.IsNullable, column.DefaultLiteral,
            column.Collation, column.DomainName, column.DefaultFunctionCalls, identity, column.MissingValueLiteral) { RecastDefault = column.RecastDefault };
        var updated = new SqlCatalogTable(plan.Table.ObjectId, plan.Table.Schema, plan.Table.Name, columns, plan.Table.PrimaryKeyColumns,
            plan.Table.Owner, plan.Table.OwningSchema, plan.Table.Constraints, plan.Table.DroppedColumnOrdinals);
        await SelfCommitAsync(statement, _catalog.ChangeIdentityAsync(plan.Table, updated, options, restart, statement.ScriptReceipt, cancellationToken)).ConfigureAwait(false);
        return new SqlQueryResult(DatabaseResultStatus.Success, 0);
    }
}
