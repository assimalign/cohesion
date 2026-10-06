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
/// The metadata catalog of one key-value database: the entry-space format version and the
/// physical registrations of the database's primary key index, persisted through the storage
/// kernel on a dedicated catalog file set so metadata gets the same durability as data.
/// </summary>
/// <remarks>
/// <para>
/// The key-value model deliberately has a <b>minimal</b> catalog: no schemas, no tables, no
/// constraints beyond key uniqueness (which the primary index itself enforces). What must
/// persist is exactly what re-attaches the database on open: the index registrations (a tree's
/// root page stays fixed through splits since #1159; the engine still re-exports at its
/// persistence points as a backstop) and the entry-space format version (records are not
/// self-describing across format changes). Catalog writes are self-committing: each runs in its
/// own storage transaction on the catalog file set and is durable when the call returns. Named
/// key spaces (multiple ordered key spaces per database) and per-entry expiration metadata are
/// deferred model features; when they land, their registrations join this catalog.
/// </para>
/// <para>
/// Records are encoded with the shared self-describing tuple codec, one for the index
/// registrations and one for the entry-space format version. They rewrite in place when they fit
/// and relocate (delete and insert) when they grow (the SQL catalog's persistence pattern, minus
/// everything relational).
/// </para>
/// <para>
/// <b>Shape (concrete-types plan, phase 4, #1260).</b> One sealed type: the former
/// <c>IKeyValueCatalog</c> interface, its internal default implementation and the static
/// <c>KeyValueCatalog</c> factory collapsed into it, and <see cref="CaptureSnapshot"/> became an
/// instance method. <see cref="Open"/> is the one way to create it.
/// </para>
/// </remarks>
public sealed class KeyValueCatalog
{
    private const int indexRegistrationsKind = 1;
    private const int entrySpaceFormatKind = 2;

    private readonly KeyValueStorage _storage;
    private readonly object _sync = new();
    private int _entrySpaceFormatVersion = 1;
    private (PageId PageId, int SlotIndex)? _registrationsLocation;
    private (PageId PageId, int SlotIndex)? _formatLocation;
    private IReadOnlyList<BTreeIndexRegistration> _registrations = Array.Empty<BTreeIndexRegistration>();

    private KeyValueCatalog(KeyValueStorage storage)
    {
        _storage = storage;
    }

    /// <summary>
    /// Gets the entry-space format version of the database's data storage: its entry records and
    /// the primary index tree that shares their file set. 2 = MVCC-stamped key/value entry records
    /// in the key space's page chain, indexed by a tree of B-tree page format 2 (#1194); 1 = the
    /// same records over a tree of B-tree page format 1, and also the value reported when no marker
    /// is persisted. The engine reads this at open and refuses a database with a primary index on
    /// any version but its own.
    /// </summary>
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

    /// <summary>
    /// Opens the catalog persisted on the given storage, loading any existing metadata records.
    /// </summary>
    /// <param name="storage">The dedicated catalog storage file set.</param>
    /// <returns>The catalog.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="storage"/> is null.</exception>
    /// <exception cref="KeyValueCatalogException">Thrown when a persisted record is malformed.</exception>
    public static KeyValueCatalog Open(KeyValueStorage storage)
    {
        ArgumentNullException.ThrowIfNull(storage);
        var catalog = new KeyValueCatalog(storage);
        catalog.Load();
        return catalog;
    }

    /// <summary>
    /// Captures the entry-space format and index registrations atomically.
    /// </summary>
    /// <returns>A read-only capture unaffected by later catalog writes.</returns>
    public KeyValueCatalogSnapshot CaptureSnapshot()
    {
        lock (_sync)
        {
            return new KeyValueCatalogSnapshot(_entrySpaceFormatVersion, Array.AsReadOnly(_registrations.ToArray()));
        }
    }

    /// <summary>
    /// Persists the entry-space format version. Self-committing, like every catalog write; called
    /// by the engine at database creation, before it registers the primary index (the space is
    /// born on the current format).
    /// </summary>
    /// <param name="version">The format version to persist.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="version"/> is zero or negative.</exception>
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

    /// <summary>
    /// Persists the physical index registrations exported by the index manager, so the primary
    /// key index re-attaches when the database reopens.
    /// </summary>
    /// <param name="registrations">The registrations to persist (replaces the stored set).</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="registrations"/> is null.</exception>
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

    /// <summary>
    /// Loads the persisted index registrations.
    /// </summary>
    /// <returns>The stored registrations, empty when none were saved.</returns>
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
        StorageTransaction transaction,
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
