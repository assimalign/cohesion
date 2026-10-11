using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.Database.Sql.Catalog.Internal;

[JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(SqlCatalogPendingMigration))]
internal sealed partial class SqlCatalogJsonContext : JsonSerializerContext
{
}
