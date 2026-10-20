using Assimalign.Cohesion.Database.Sql.Catalog;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed record SqlOuterReference(int Scope, SqlBoundExpression Value, SqlCatalogColumn Column);
