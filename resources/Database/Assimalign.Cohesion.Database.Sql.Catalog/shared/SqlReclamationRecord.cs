using System.IO;

using Assimalign.Cohesion.Database.Types;

// Deviates from the repo namespace-matches-assembly rule: this stateless durable-record codec
// is shared with SQL's worker; no instance or mutable state crosses an assembly boundary.
namespace Assimalign.Cohesion.Database.Sql.Catalog.Internal;

internal static class SqlReclamationRecord
{
    internal const int Kind = 14;

    internal static byte[] Encode(ulong objectId, string schema, string name)
        => new DatabaseKeyWriter().AppendInt32(Kind).AppendInt64((long)objectId)
            .AppendString(schema, Collation.Binary).AppendString(name, Collation.Binary).ToArray();

    internal static (ulong ObjectId, string Schema, string Name) Decode(ref DatabaseKeyReader reader)
    {
        ulong objectId = (ulong)reader.ReadInt64();
        string schema = reader.ReadString(out _);
        string name = reader.ReadString(out _);
        if (objectId == 0 || objectId == ulong.MaxValue || string.IsNullOrWhiteSpace(schema)
            || string.IsNullOrWhiteSpace(name) || !reader.IsAtEnd)
        {
            throw new InvalidDataException();
        }
        return (objectId, schema, name);
    }
}
