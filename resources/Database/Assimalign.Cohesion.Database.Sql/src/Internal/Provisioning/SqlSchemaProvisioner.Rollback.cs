using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Schema;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlSchemaProvisioner
{
    private string DescribeHeldSchema(SqlCompiledSchema schema, SqlCatalogSchemaState? stored,
        IReadOnlyList<SqlSchemaMigrationOperation> refused, bool rollback)
        => string.Format(CultureInfo.CurrentCulture, ErrorMessages.SchemaHeld,
            Describe(), schema.Hash, stored?.ContentHash ?? "(none)",
            rollback ? ErrorMessages.RollbackSchemaHeld : ErrorMessages.NewSchemaHeld,
            string.Join("; ", refused.Select(Describe)));

    private SqlSchemaMigrationResult HoldSchema(SqlCompiledSchema schema, SqlCatalogSchemaState? stored,
        SqlSchemaMigrationPlan plan, IReadOnlyList<SqlSchemaMigrationOperation> refused, bool rollback)
    {
        string code = rollback ? SqlProvisioningCodes.RollbackHeld : SqlProvisioningCodes.PolicyRefused;
        string detail = DescribeHeldSchema(schema, stored, refused, rollback);
        _database.ReportProvisioning(new DatabaseProvisioningProgress("SchemaHeld", schema.Name, 1,
            plan.Operations.Count, false, failureCode: code, detail: detail));
        TraceStage("SchemaHeld", code, stepCount: plan.Operations.Count);
        TraceOutcome("Held");
        return new SqlSchemaMigrationResult(stored?.ContentHash, schema.Hash, 0, WasAlreadyApplied: false);
    }
}
