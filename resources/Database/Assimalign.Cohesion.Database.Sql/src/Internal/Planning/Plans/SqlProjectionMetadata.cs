using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>Projected expression metadata retained across derived materialization.</summary>
internal sealed record SqlProjectionMetadata(SqlCollationCandidate Collation, SqlCatalogColumn? SourceColumn,
    DatabaseTypeInfo? TypeInfo = null, string? DomainName = null);
