using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Indexing;
using Assimalign.Cohesion.Database.KeyValuePair.Storage;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.KeyValuePair.Catalog;

/// <summary>
/// Default key-value catalog: metadata records on a dedicated catalog storage
/// file set, encoded with the shared self-describing tuple codec — one record for
/// the index registrations and one for the entry-space format version. Records
/// rewrite in place when they fit and relocate (delete + insert) when they grow
/// (the `DefaultSqlCatalog` persistence pattern, minus everything relational).
/// </summary>
internal sealed class DefaultKeyValueCatalog : IKeyValueCatalog
{
    private const int indexRegistrationsKind = 1;
    private const int entrySpaceFormatKind = 2;

    private readonly KeyValueStorage _storage;
    private readonly object _sync = new();
    private int _entrySpaceFormatVersion = 1;
    private (PageId PageId, int SlotIndex)? _registrationsLocation;
    private (PageId PageId, int SlotIndex)? _formatLocation;
    private IReadOnlyList<BTreeIndexRegistration> _registrations = Array.Empty<BTreeIndexRegistration>();

    private DefaultKeyValueCatalog(KeyValueStorage storage)
    {
        _storage = storage;
    }

    internal static DefaultKeyValueCatalog Open(KeyValueStorage storage)
    {
        var catalog = new DefaultKeyValueCatalog(storage);
        catalog.Load();
        return catalog;
    }

    /// <inheritdoc />
    public int EntrySpaceFormatVersion
    {
        get
        {
            lock (_sync)
            {
                return _entrySpaceFormatVersion;
            }
        }
    }

    /// <inheritdoc />
    public ValueTask SetEntrySpaceFormatVersionAsync(int version, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(version);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {
            var writer = new DatabaseKeyWriter();
            writer.AppendInt32(entrySpaceFormatKind).AppendInt32(version);
            byte[] record = writer.ToArray();

            using (var transaction = _storage.BeginTransaction())
            {
                _formatLocation = UpsertRecord(transaction, _formatLocation, record);
                transaction.Commit();
            }

            _entrySpaceFormatVersion = version;
            return default;
        }
    }

    /// <inheritdoc />
    public ValueTask SaveIndexRegistrationsAsync(IReadOnlyList<BTreeIndexRegistration> registrations, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {
            byte[] record = EncodeRegistrations(registrations);

            using (var transaction = _storage.BeginTransaction())
            {
                _registrationsLocation = UpsertRecord(transaction, _registrationsLocation, record);
                transaction.Commit();
            }

            _registrations = registrations.ToList();
            return default;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<BTreeIndexRegistration> GetIndexRegistrations()
    {
        lock (_sync)
        {
            return _registrations;
        }
    }

    // ── Persistence ────────────────────────────────────────────────────

    private void Load()
    {
        using var iterator = _storage.GetUnitIterator();

        while (iterator.MoveNext())
        {
            var unit = iterator.Current;
            var reader = new DatabaseKeyReader(unit.Data.Span);
            int kind = reader.ReadInt32();

            switch (kind)
            {
                case indexRegistrationsKind:
                    _registrations = DecodeRegistrations(ref reader);
                    _registrationsLocation = (unit.PageId, unit.SlotIndex);
                    break;

                case entrySpaceFormatKind:
                    _entrySpaceFormatVersion = reader.ReadInt32();
                    _formatLocation = (unit.PageId, unit.SlotIndex);
                    break;

                default:
                    throw new KeyValueCatalogException($"Malformed catalog record of kind {kind}.");
            }
        }
    }

    /// <summary>
    /// Rewrites a record in place when it fits, relocating it otherwise.
    /// </summary>
    private (PageId PageId, int SlotIndex) UpsertRecord(
        IStorageTransaction transaction,
        (PageId PageId, int SlotIndex)? location,
        byte[] record)
    {
        if (location is null)
        {
            return _storage.InsertEntry(transaction, record);
        }

        try
        {
            _storage.UpdateEntry(transaction, location.Value.PageId, location.Value.SlotIndex, record);
            return location.Value;
        }
        catch (SlottedPageException)
        {
            _storage.DeleteEntry(transaction, location.Value.PageId, location.Value.SlotIndex);
            return _storage.InsertEntry(transaction, record);
        }
    }

    private static byte[] EncodeRegistrations(IReadOnlyList<BTreeIndexRegistration> registrations)
    {
        var writer = new DatabaseKeyWriter();
        writer.AppendInt32(indexRegistrationsKind).AppendInt32(registrations.Count);

        foreach (var registration in registrations)
        {
            writer.AppendInt64((long)registration.ObjectId)
                  .AppendString(registration.Definition.Name, Collation.Binary)
                  .AppendInt8((sbyte)registration.Definition.Kind)
                  .AppendBoolean(registration.Definition.IsUnique)
                  .AppendInt64(registration.RootPageId);
        }

        return writer.ToArray();
    }

    private static IReadOnlyList<BTreeIndexRegistration> DecodeRegistrations(ref DatabaseKeyReader reader)
    {
        int count = reader.ReadInt32();
        var registrations = new List<BTreeIndexRegistration>(count);

        for (int i = 0; i < count; i++)
        {
            ulong objectId = (ulong)reader.ReadInt64();
            string indexName = reader.ReadString(out _);
            var kind = (IndexKind)reader.ReadInt8();
            bool unique = reader.ReadBoolean();
            long rootPageId = reader.ReadInt64();

            registrations.Add(new BTreeIndexRegistration(
                objectId, new IndexDefinition(indexName, kind, unique), rootPageId));
        }

        return registrations;
    }
}
