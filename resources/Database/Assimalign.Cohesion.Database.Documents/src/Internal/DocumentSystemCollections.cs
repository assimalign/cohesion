using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using Assimalign.Cohesion.Database.Documents.Catalog;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Documents.Internal;

// These names describe document-shaped catalog projections, never stored collections.
internal static class DocumentSystemCollections
{
    internal const string Indexes = "COHESION_SCHEMA.INDEXES";
    internal const string Ownership = "COHESION_SCHEMA.OBJECT_OWNERSHIP";

    internal static string? Find(string name)
        => string.Equals(name, Indexes, StringComparison.OrdinalIgnoreCase) ? Indexes :
            string.Equals(name, Ownership, StringComparison.OrdinalIgnoreCase) ? Ownership : null;

    internal static void EnsureReadOnly(string name)
    {
        if (Find(name) is string systemName)
        {
            throw new DatabaseException($"System collection '{systemName}' is read-only.");
        }
    }

    internal static IEnumerable<JsonElement> Enumerate(DocumentDatabaseInstance database, TransactionSnapshot snapshot,
        string name, CancellationToken cancellationToken)
    {
        // Every lookup uses the same statement snapshot, including a transaction's
        // own writes. No content records or synthetic catalog entries are created.
        foreach (var collection in database.Catalog.GetCollections(snapshot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (name == Ownership)
            {
                yield return OwnershipDocument(database.Name.ToString(), collection);
            }
            else
            {
                foreach (var index in database.Catalog.GetIndexes(collection.Id, snapshot))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    yield return IndexDocument(database.Name.ToString(), collection.Name, index);
                }
            }
        }
    }

    private static JsonElement IndexDocument(string database, string collection, DocumentIndexMetadata index)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("COLLECTION_CATALOG", database);
            writer.WriteString("COLLECTION_NAME", collection);
            writer.WriteString("INDEX_NAME", index.Name);
            writer.WriteString("PATH", index.Path);
            writer.WriteBoolean("IS_UNIQUE", false);
            writer.WriteEndObject();
        }
        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    private static JsonElement OwnershipDocument(string database, DocumentCollectionMetadata collection)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("COLLECTION_CATALOG", database);
            writer.WriteString("COLLECTION_NAME", collection.Name);
            writer.WriteString("OBJECT_TYPE", "COLLECTION");
            writer.WriteString("OBJECT_NAME", collection.Name);
            writer.WriteString("OWNER", collection.Owner == DatabaseObjectOwner.Schema ? "Schema" : "Adhoc");
            writer.WriteString("OWNING_SCHEMA", collection.OwningSchema);
            writer.WriteEndObject();
        }
        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }
}
