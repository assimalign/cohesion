using System;
using System.Collections.Generic;
using System.Linq;

using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Catalog;

public sealed partial class SqlCatalog
{
    // Format 38. Bounded chunks grow the catalog rather than evicting identities.
    private const int schemaHistoryFormatVersion = 38;
    private const int schemaHistoryRecordKind = 20;
    private const int schemaHistoryChunkEntries = 32;
    private int _schemaHistoryTailChunk = -1;
    private readonly Dictionary<string, (SqlAppliedSchemaHistoryEntry Entry, int Chunk)> _schemaHistory = new(StringComparer.Ordinal);
    private readonly List<(SqlAppliedSchemaHistoryEntry[] Entries, (PageId PageId, int SlotIndex) Location)> _schemaHistoryChunks = [];

    internal bool HasAppliedSchema(string hash)
    {
        lock (_sync) { ThrowIfOffline(); return _schemaHistory.ContainsKey(hash); }
    }

    internal SqlAppliedSchemaHistoryEntry[] AppliedSchemaHistory
    {
        get { lock (_sync) { ThrowIfOffline(); return _schemaHistory.Values.Select(value => value.Entry).ToArray(); } }
    }

    // The caller publishes this preallocated cache change only after its kind-6 commit.
    // No independent history commit, checkpoint, or durable boundary exists.
    private Action StageSchemaHistory(StorageTransaction transaction, string hash, ulong generation)
    {
        bool seen = _schemaHistory.TryGetValue(hash, out var previous);
        int chunk = seen ? previous.Chunk : _schemaHistoryTailChunk >= 0 ? _schemaHistoryTailChunk : _schemaHistoryChunks.Count;
        bool append = chunk == _schemaHistoryChunks.Count;
        var entries = append ? [] : _schemaHistoryChunks[chunk].Entries;
        var entry = new SqlAppliedSchemaHistoryEntry(hash, seen ? previous.Entry.FirstAppliedGeneration : generation, generation);
        var replacement = seen ? entries.Select(value => value.ContentHash == hash ? entry : value).ToArray()
            : entries.Append(entry).ToArray();
        var writer = new DatabaseKeyWriter();
        writer.AppendInt32(schemaHistoryRecordKind).AppendInt32(replacement.Length);
        foreach (var value in replacement)
        {
            writer.AppendString(value.ContentHash, Collation.Binary)
                .AppendInt64(unchecked((long)value.FirstAppliedGeneration)).AppendInt64(unchecked((long)value.LastAppliedGeneration));
        }
        byte[] record = writer.ToArray();
        EnsureStorable(record, "applied-schema history chunk");
        _schemaHistory.EnsureCapacity(_schemaHistory.Count + (seen ? 0 : 1));
        _schemaHistoryChunks.EnsureCapacity(_schemaHistoryChunks.Count + (append ? 1 : 0));
        var location = UpsertRecord(transaction, append ? null : _schemaHistoryChunks[chunk].Location, record);
        return () =>
        {
            if (append) { _schemaHistoryChunks.Add((replacement, location)); }
            else { _schemaHistoryChunks[chunk] = (replacement, location); }
            _schemaHistory[hash] = (entry, chunk);
            if (!seen) { _schemaHistoryTailChunk = replacement.Length < schemaHistoryChunkEntries ? chunk : -1; }
        };
    }

    private void LoadSchemaHistory(ref DatabaseKeyReader reader, (PageId PageId, int SlotIndex) location)
    {
        int count = reader.ReadInt32();
        if (count <= 0 || count > schemaHistoryChunkEntries)
        { throw new SqlCatalogException("CS06059", ErrorMessages.SchemaHistoryInvalid); }
        var entries = new SqlAppliedSchemaHistoryEntry[count];
        for (int index = 0; index < count; index++)
        {
            string hash = reader.ReadString(out _);
            ulong first = unchecked((ulong)reader.ReadInt64());
            ulong last = unchecked((ulong)reader.ReadInt64());
            if (string.IsNullOrWhiteSpace(hash) || first > last || _schemaHistory.ContainsKey(hash))
            { throw new SqlCatalogException("CS06059", ErrorMessages.SchemaHistoryInvalid); }
            entries[index] = new SqlAppliedSchemaHistoryEntry(hash, first, last);
            _schemaHistory.Add(hash, (entries[index], _schemaHistoryChunks.Count));
        }
        if (count < schemaHistoryChunkEntries) { _schemaHistoryTailChunk = _schemaHistoryChunks.Count; }
        _schemaHistoryChunks.Add((entries, location));
    }

}
