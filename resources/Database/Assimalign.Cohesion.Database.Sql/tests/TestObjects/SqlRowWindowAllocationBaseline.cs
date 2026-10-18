#if !DEBUG
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;

namespace Assimalign.Cohesion.Database.Sql.Tests.TestObjects;

// The pre-window dispatcher for these three statement shapes. Both arms use the current
// statement cores; only the row-window startup call is absent from this reference.
internal static class SqlRowWindowAllocationBaseline
{
    internal static async Task<DatabaseResult> ExecuteAsync(SqlPlanExecutor executor, SqlPlan plan,
        SqlStatementContext statement, CancellationToken cancellationToken)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        switch (plan)
        {
            case SqlInsertSelectPlan insert:
                return await ExecuteInsertSelectAsync(executor, insert, statement, cancellationToken).ConfigureAwait(false);
            case SqlSelectPlan select:
                return ExecuteSelect(executor, select, statement, cancellationToken);
            case SqlGroupPlan group:
                return await ExecuteGroupAsync(executor, group, statement, cancellationToken).ConfigureAwait(false);
            default:
                throw new System.InvalidOperationException("Unexpected allocation reference plan.");
        }
    }

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "ExecuteInsertSelectAsync")]
    private static extern Task<DatabaseResult> ExecuteInsertSelectAsync(SqlPlanExecutor executor, SqlInsertSelectPlan plan,
        SqlStatementContext statement, CancellationToken cancellationToken);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "ExecuteSelect")]
    private static extern DatabaseResult ExecuteSelect(SqlPlanExecutor executor, SqlSelectPlan plan,
        SqlStatementContext statement, CancellationToken cancellationToken);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "ExecuteGroupAsync")]
    private static extern Task<DatabaseResult> ExecuteGroupAsync(SqlPlanExecutor executor, SqlGroupPlan plan,
        SqlStatementContext statement, CancellationToken cancellationToken);
}
#endif
