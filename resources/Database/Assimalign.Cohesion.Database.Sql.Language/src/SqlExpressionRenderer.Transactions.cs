using System;
using System.Linq;

namespace Assimalign.Cohesion.Database.Sql.Language;

public static partial class SqlExpressionRenderer
{
    /// <summary>Renders transaction control as canonical SQL without a terminator.</summary>
    /// <param name="query">The parsed transaction control.</param>
    /// <returns>The canonical transaction statement.</returns>
    /// <exception cref="ArgumentNullException">The query is null.</exception>
    public static string Render(SqlTransactionExpression query)
    {
        ArgumentNullException.ThrowIfNull(query);
        string command = query.CommandType switch
        {
            SqlQueryCommandType.Begin => "BEGIN",
            SqlQueryCommandType.SetTransaction => "SET TRANSACTION",
            SqlQueryCommandType.Commit => "COMMIT",
            SqlQueryCommandType.Rollback => "ROLLBACK",
            _ => "DISCARD ALL",
        };
        return command + (query.IsolationLevels.Count == 0 ? "" : " " + string.Join(", ", query.IsolationLevels.Select(level =>
            "ISOLATION LEVEL " + (level switch
            {
                SqlTransactionIsolationLevel.ReadUncommitted => "READ UNCOMMITTED",
                SqlTransactionIsolationLevel.ReadCommitted => "READ COMMITTED",
                SqlTransactionIsolationLevel.RepeatableRead => "REPEATABLE READ",
                _ => "SERIALIZABLE",
            }))));
    }
}
