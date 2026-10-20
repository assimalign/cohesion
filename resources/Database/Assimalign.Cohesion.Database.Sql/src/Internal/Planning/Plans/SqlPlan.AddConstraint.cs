using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Language;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed record SqlAddConstraintPlan(SqlCatalogTable Table, SqlConstraintDefinition Constraint) : SqlPlan;
