using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Sql.Schema.Internal;

[global::System.Diagnostics.DebuggerDisplay("SqlCompiledSchemaJsonContext")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Schema.Internal.SqlCompiledSchemaJsonContext.DebugView))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = false,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(SqlCompiledSchema))]
internal sealed partial class SqlCompiledSchemaJsonContext : JsonSerializerContext{
    private sealed class DebugView
    {
        public DebugView(global::Assimalign.Cohesion.Database.Sql.Schema.Internal.SqlCompiledSchemaJsonContext value)
        {
            _ = value;
        }
    }
}
