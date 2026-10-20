using Assimalign.Cohesion.Database.Sql.Catalog;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>A USING output's metadata and bound value, independent of qualified physical columns.</summary>
internal sealed record SqlUsingColumn(SqlCatalogColumn Column, SqlBoundExpression Value);
