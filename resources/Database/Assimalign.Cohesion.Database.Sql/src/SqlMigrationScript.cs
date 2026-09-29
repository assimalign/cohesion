using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Database.Sql.Schema;

namespace Assimalign.Cohesion.Database.Sql;

/// <summary>Contains deterministic SQL requests for one ordered schema migration plan.</summary>
public sealed class SqlMigrationScript
{
    internal SqlMigrationScript(SqlSchemaMigrationPlan plan, IReadOnlyList<SqlMigrationScriptStep> steps)
    {
        Plan = plan;
        var copy = new SqlMigrationScriptStep[steps.Count];
        for (int index = 0; index < steps.Count; index++)
        {
            copy[index] = steps[index];
        }

        Steps = Array.AsReadOnly(copy);
    }

    /// <summary>Gets the portable migration plan rendered by this script.</summary>
    public SqlSchemaMigrationPlan Plan { get; }

    /// <summary>Gets the SQL requests in deterministic execution order.</summary>
    public IReadOnlyList<SqlMigrationScriptStep> Steps { get; }

    /// <summary>Gets a value indicating whether every step has a compensating request.</summary>
    public bool IsReversible
    {
        get
        {
            foreach (SqlMigrationScriptStep step in Steps)
            {
                if (step.RollbackRequest is null)
                {
                    return false;
                }
            }

            return true;
        }
    }
}

/// <summary>Pairs one portable migration operation with executable SQL and its compensation.</summary>
public sealed class SqlMigrationScriptStep
{
    internal SqlMigrationScriptStep(
        SqlSchemaMigrationOperation operation,
        string statementText,
        string? rollbackStatementText)
    {
        Operation = operation;
        StatementText = statementText;
        Request = SqlQueryRequest.FromSql(statementText);
        RollbackStatementText = rollbackStatementText;
        RollbackRequest = rollbackStatementText is null
            ? null
            : SqlQueryRequest.FromSql(rollbackStatementText);
    }

    /// <summary>Gets the portable operation represented by this step.</summary>
    public SqlSchemaMigrationOperation Operation { get; }

    /// <summary>Gets the deterministic SQL statement text.</summary>
    public string StatementText { get; }

    /// <summary>Gets the parsed SQL request ready for execution.</summary>
    public SqlQueryRequest Request { get; }

    /// <summary>Gets the deterministic compensating SQL statement, when the step is reversible.</summary>
    public string? RollbackStatementText { get; }

    /// <summary>Gets the parsed compensating SQL request, when the step is reversible.</summary>
    public SqlQueryRequest? RollbackRequest { get; }
}
