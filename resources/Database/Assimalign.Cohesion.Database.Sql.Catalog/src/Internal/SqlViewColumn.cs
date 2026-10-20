using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Catalog.Internal;

internal sealed record SqlViewColumn(string Name, DatabaseTypeInfo Type, string? Collation, string? DomainName);
