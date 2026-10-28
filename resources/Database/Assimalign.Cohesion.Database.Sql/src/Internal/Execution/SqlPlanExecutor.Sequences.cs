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

    private async Task<DatabaseResult> ExecuteSequenceDdlAsync(SqlSequencePlan plan, SqlStatementContext statement, CancellationToken cancellationToken)
    {
        if (statement.ScriptReceipt is not null)
        { throw new SqlPlanningException("CS03074", ErrorMessages.SequencePersistedUnsupported); }
        if (plan.Expression is SqlCreateSequenceExpression create)
        {
            string schema = create.Sequence.SchemaName ?? SqlPlanner.DefaultSchema;
            var options = SequenceOptions(create.Options, null);
            if (_catalog.FindSequence(schema, create.Sequence.TableName) is not null && create.IfNotExists)
            { return new SqlQueryResult(DatabaseResultStatus.Success, 0); }
            await SelfCommitAsync(statement, _catalog.CreateSequenceAsync(schema, create.Sequence.TableName, options, cancellationToken)).ConfigureAwait(false);
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
        }
        using var metadata = statement.DdlInterlock?.EnterMetadata(statement.DatabaseName, definition: true);
        var resolved = new List<SqlCatalogSequence>();
        for (int i = 0; i < names.Count; i++)
        {
            var sequence = _catalog.FindSequence(names[i].SchemaName ?? SqlPlanner.DefaultSchema, names[i].TableName);
            if (sequence?.ObjectId != expected[i]?.ObjectId) { throw new SqlPlanningException("CS04067", ErrorMessages.SequenceChanged); }
            if (sequence is not null) { resolved.Add(sequence); }
        }
        if (plan.Expression is SqlAlterSequenceExpression alteration && resolved.Count > 0)
        {
            var options = SequenceOptions(alteration.Options, resolved[0].Options);
            var restart = alteration.Options.FirstOrDefault(option => option.Name == "RESTART");
            await SelfCommitAsync(statement, _catalog.AlterSequenceAsync(resolved[0], options,
                restart is null ? null : restart.Value ?? options.Start, cancellationToken)).ConfigureAwait(false);
        }
        else if (plan.Expression is SqlDropSequenceExpression && resolved.Count > 0)
        {
            await SelfCommitAsync(statement, _catalog.DropSequencesAsync(resolved.DistinctBy(s => s.ObjectId).ToArray(), cancellationToken)).ConfigureAwait(false);
        }
        return new SqlQueryResult(DatabaseResultStatus.Success, 0);
    }

    private static SqlSequenceOptions SequenceOptions(IReadOnlyList<SqlSequenceOption> options, SqlSequenceOptions? previous)
    {
        SqlSequenceOption? Find(string name) => options.FirstOrDefault(option => option.Name == name);
        var typeOption = Find("AS");
        DatabaseType type = typeOption is null ? previous?.Type ?? DatabaseType.Int64 : typeOption.TypeName switch
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
            Find("CACHE")?.Value ?? previous?.Cache ?? 1, Find("CYCLE") is { } cycle ? cycle.Value == 1 : previous?.Cycle ?? false);
    }
}
