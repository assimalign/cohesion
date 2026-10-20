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
