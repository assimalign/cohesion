using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.Database.Sql.Catalog.Internal;

[JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(SqlCatalogPendingMigration))]
[JsonSerializable(typeof(SqlCatalogScript))]
[JsonSerializable(typeof(SqlCatalogFunction))]
[JsonSerializable(typeof(SqlCatalogDomain))]
[JsonSerializable(typeof(SqlCatalogNamespace))]
[JsonSerializable(typeof(SqlViewRecord))]
internal sealed partial class SqlCatalogJsonContext : JsonSerializerContext
{
}
