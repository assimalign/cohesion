using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlPlanExecutor
{
    // Used only while a blocking aggregate consumes its child inside the statement.
    // Final SELECT/DML results still complete before statement resources unwind.
    private async Task<DatabaseResult> RecursiveGroupInputAsync(SqlGroupPlan group, SqlStatementContext statement, CancellationToken cancellationToken)
    {
        // These native accumulators contain only a count and a numeric sum. Other
        // accumulators can retain arbitrary values or application-owned state, so
        // keep their existing completed input and account for its actual lifetime.
        bool streaming = true;
        foreach (var aggregate in group.Aggregates)
        {
            if (aggregate.Function is not (SqlCountFunction or SqlSumFunction)) { streaming = false; break; }
        }
        if (streaming)
        {
            return new RecursiveInputResultSet(((SqlRelationPlan)StatementInput(group.Input)).OutputProjections,
                ReadRecursiveQueryAsync(group.Input, _subqueryValues, statement, cancellationToken));
        }
        var input = (SqlMaterializedResultSet)await ExecuteAsync(group.Input, statement, cancellationToken).ConfigureAwait(false);
        var memory = RecursiveScratch!;
        memory.Reserve(input.RowBufferBytes);
        foreach (var row in input.Rows) { memory.Retain(row); }
        return input;
    }

    [System.Diagnostics.DebuggerDisplay("RecursiveInputResultSet")]
    [System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
    private sealed class RecursiveInputResultSet : DatabaseResultSet
    {
        private readonly IAsyncEnumerable<object?[]> _rows;
        internal RecursiveInputResultSet(IReadOnlyList<SqlProjection> projections, IAsyncEnumerable<object?[]> rows)
        {
            var columns = new DatabaseColumn[projections.Count];
            for (int index = 0; index < columns.Length; index++)
            {
                var projection = projections[index];
                columns[index] = new DatabaseColumn
                {
                    Name = projection.Name, Ordinal = index, Type = projection.Type,
                    MaxLength = projection.MaxLength, IsNullable = projection.IsNullable,
                };
            }
            Columns = columns; _rows = rows;
        }
        public override DatabaseResultStatus Status => DatabaseResultStatus.Success;
        public override long AffectedCount => -1;
        public override IReadOnlyList<Diagnostic> Diagnostics => [];
        public override IReadOnlyList<DatabaseColumn> Columns { get; }
        public override async IAsyncEnumerable<DatabaseRow> GetRowsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var row in _rows.WithCancellation(cancellationToken).ConfigureAwait(false))
            { yield return new SqlMaterializedRow(row); }
        }
        public override ValueTask DisposeAsync() => default;
        private sealed class DebugView { public DebugView(RecursiveInputResultSet value) { _ = value; } }
    }
}
