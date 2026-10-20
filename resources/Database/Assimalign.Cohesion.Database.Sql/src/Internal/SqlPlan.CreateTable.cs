using System.Collections.Generic;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Language;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed record SqlCreateTablePlan(
    string Schema,
    string Name,
    IReadOnlyList<SqlCatalogColumn> Columns,
    IReadOnlyList<string> PrimaryKey,
    bool IfNotExists,
    IReadOnlyList<SqlConstraintDefinition> Constraints) : SqlPlan;
