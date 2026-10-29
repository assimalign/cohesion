using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Sql.Language;

/// <summary>Represents BEGIN, SET TRANSACTION, COMMIT, ROLLBACK, or DISCARD ALL session control.</summary>
public sealed class SqlTransactionExpression : SqlQueryExpression
{
    internal SqlTransactionExpression(SqlQueryCommandType commandType, Location location,
        IReadOnlyList<SqlTransactionIsolationLevel>? isolationLevels = null)
        : base(commandType, null, location)
    {
        IsolationLevels = isolationLevels ?? Array.Empty<SqlTransactionIsolationLevel>();
    }

    /// <summary>Gets the isolation modes in source order. Repeated modes use the last level.</summary>
    public IReadOnlyList<SqlTransactionIsolationLevel> IsolationLevels { get; }
}
