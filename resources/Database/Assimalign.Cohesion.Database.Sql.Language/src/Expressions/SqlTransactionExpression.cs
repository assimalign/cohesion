using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Sql.Language;

/// <summary>Represents BEGIN, SET TRANSACTION, COMMIT, ROLLBACK, or DISCARD ALL session control.</summary>
public sealed class SqlTransactionExpression : SqlQueryExpression
{
    internal SqlTransactionExpression(SqlQueryCommandType commandType, Location location,
        IReadOnlyList<SqlTransactionIsolationLevel>? isolationLevels = null, string? savepointName = null)
        : base(commandType, null, location)
    {
        SavepointName = savepointName;
        IsolationLevels = isolationLevels ?? Array.Empty<SqlTransactionIsolationLevel>();
    }

    /// <summary>Gets the normalized savepoint identifier, or null for another control.</summary>
    public string? SavepointName { get; }

    /// <summary>Gets the isolation modes in source order. Repeated modes use the last level.</summary>
    public IReadOnlyList<SqlTransactionIsolationLevel> IsolationLevels { get; }
}
