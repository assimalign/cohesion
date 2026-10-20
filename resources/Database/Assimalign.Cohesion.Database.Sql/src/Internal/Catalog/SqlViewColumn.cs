using Assimalign.Cohesion.Database.Types;

using Assimalign.Cohesion.Database.Sql.Catalog;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed record SqlViewColumn(string Name, DatabaseTypeInfo Type, string? Collation, string? DomainName);
