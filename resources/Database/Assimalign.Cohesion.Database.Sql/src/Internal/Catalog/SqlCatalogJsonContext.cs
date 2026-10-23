using System.Text.Json.Serialization;

using Assimalign.Cohesion.Database.Sql.Catalog;

namespace Assimalign.Cohesion.Database.Sql.Internal;

[global::System.Diagnostics.DebuggerDisplay("SqlCatalogJsonContext")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Internal.SqlCatalogJsonContext.DebugView))]
[JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(SqlCatalogPendingMigration))]
[JsonSerializable(typeof(SqlCatalogScript))]
[JsonSerializable(typeof(SqlCatalogFunction))]
[JsonSerializable(typeof(SqlCatalogDomain))]
[JsonSerializable(typeof(SqlCatalogNamespace))]
[JsonSerializable(typeof(SqlViewRecord))]
internal sealed partial class SqlCatalogJsonContext : JsonSerializerContext
{

    private sealed class DebugView
    {
        public DebugView(global::Assimalign.Cohesion.Database.Sql.Internal.SqlCatalogJsonContext value)
        {
            _ = value;
        }
    }
}
