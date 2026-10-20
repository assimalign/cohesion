using System;

using Assimalign.Cohesion.Database.Sql.Schema;

namespace Assimalign.Cohesion.Database.Sql.Internal;

// Allocated only while the source is enabled. Kept outside async state machines so the
// disabled path retains the original machine size, including in Debug.
internal sealed class SqlProvisioningTrace
{
    internal SqlProvisioningTrace(SqlCompiledSchema schema)
    {
        Schema = schema;
    }

    internal SqlCompiledSchema Schema { get; }
    internal long Started { get; set; }
    internal int StepCount { get; set; } = -1;
    internal int Position { get; set; }
    internal string Kind { get; set; } = "Planning";
    internal string Table { get; set; } = string.Empty;
    internal string ObjectName { get; set; } = string.Empty;
    internal string Code { get; set; } = string.Empty;
    internal string Outcome { get; set; } = "Error";
    internal Exception? Failure { get; set; }
    internal long RowsProcessed { get; set; } = -1;
    internal long TotalRows { get; set; } = -1;
    internal long LastProgress { get; set; }
}
