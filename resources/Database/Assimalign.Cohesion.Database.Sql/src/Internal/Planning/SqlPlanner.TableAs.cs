using System;
using System.Globalization;
using System.Linq;

using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlPlanner
{
    private bool _inferringTableOutput;

    private SqlCreateTableAsPlan PlanCreateTableAs(SqlCreateTableAsExpression create)
    {
        string schema = SqlNamespaces.Require(_catalog, create.Table.SchemaName ?? DefaultSchema).Name;
        if (_catalog.TryGetTable(schema, create.Table.TableName, out _))
        {
            if (create.IfNotExists) { return new(schema, create.Table.TableName, [], null, create.WithData, true); }
            throw new SqlPlanningException("CS03049", string.Format(CultureInfo.CurrentCulture,
                ErrorMessages.RenameTableCollision, schema, create.Table.TableName), create.Location);
        }
        _inferringTableOutput = true;
        try
        {
            var child = PlanQuery(create.Query);
            var output = QueryOutput(child).Projections;
            if (output.Count == 0 || (create.Columns.Count != 0 && create.Columns.Count != output.Count) ||
                (create.Columns.Count == 0 && output.Any(column => !column.HasOutputName)) ||
                (create.Columns.Count == 0 ? output.Select(column => column.Name) : create.Columns)
                    .Any(string.IsNullOrWhiteSpace) ||
                (create.Columns.Count == 0 ? output.Select(column => column.Name) : create.Columns)
                    .Distinct(StringComparer.OrdinalIgnoreCase).Count() != output.Count)
            {
                throw new SqlPlanningException("CS03068", ErrorMessages.TableAsOutputNames, create.Location);
            }
            // Like persisted views and PostgreSQL CTAS, column NOT NULL is not copied.
            // Domain identity/constraints remain part of the output's type. Unknown
            // all-NULL output becomes text, as it does for an unknown set-operation output.
            var columns = BindDerivedOutput(child, create.Table.TableName, create.Columns, create.Query).Columns
                .Select(column => new SqlCatalogColumn(column.Name,
                    column.Type.Type == DatabaseType.Null ? new DatabaseTypeInfo(DatabaseType.String) : column.Type,
                    collation: column.Collation, domainName: column.DomainName)).ToArray();
            return new(schema, create.Table.TableName, columns, child, create.WithData, create.IfNotExists);
        }
        finally { _inferringTableOutput = false; }
    }
}
