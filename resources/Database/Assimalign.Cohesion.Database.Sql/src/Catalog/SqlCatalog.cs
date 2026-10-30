using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Indexing;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Storage;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Storage.Units;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Catalog;

/// <summary>
/// The relational catalog of one SQL database: schemas, tables, columns,
/// constraints, and the physical registrations of the database's indexes —
/// persisted through the storage kernel so metadata gets the same durability as
/// data.
/// </summary>
/// <remarks>
/// <para>
/// The catalog owns its own storage instance — separate from the database's data
/// file set — so metadata records and user rows never share a scan space, while
/// still getting the same page/WAL durability. The engine composes one catalog
/// storage per database: the <c>catalog</c> file set in the database's directory,
/// beside its data set, labeled <c>{database}/catalog</c> in its storage header.
/// </para>
/// <para>
/// DDL operations are self-committing: each runs in its own storage transaction and
/// is durable when the call returns. Interleaving DDL with an open DML transaction
/// is deliberately not supported in the MVP (the catalog cache updates on commit,
/// and half-visible schema changes are a correctness trap). The engine locks the
/// affected tables; this catalog serializes metadata publication under its lock.
/// </para>
/// <para>
/// A commit whose record was appended but whose durability was not confirmed takes
/// this catalog's storage offline. Every subsequent catalog read and mutation throws
/// <see cref="StorageOfflineException"/> until the database is reopened: its cached
/// identities, generations and record locations may no longer describe durable state.
/// </para>
/// <para>
/// Records are encoded with the shared self-describing tuple codec, one record per
/// table plus one for the object-id counter and one for index registrations. They
/// rewrite in place when they fit and relocate (delete and insert) when they grow.
/// </para>
/// <para>
/// <b>Shape (concrete-types plan, phase 4, #1260).</b> One sealed type: the former
/// <c>ISqlCatalog</c> interface, its internal default implementation and the static
/// <c>SqlCatalog</c> class collapsed into it. <see cref="CaptureSnapshot"/>,
/// <see cref="ReserveTableAsync"/>, <see cref="PublishTableAsync"/> and
/// <see cref="DropConstraintAsync"/> were statics over the interface, kept off it so other
/// implementations need not honour them; with one type they are instance methods.
/// The two <c>Open</c> overloads, <see cref="Open(SqlStorage)"/> and
/// <see cref="Open(SqlStorage, Collation)"/>, are the only ways to create it.
/// </para>
/// </remarks>
[global::System.Diagnostics.DebuggerDisplay("SqlCatalog")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Catalog.SqlCatalog.DebugView))]
public sealed partial class SqlCatalog
{
    private const int tableRecordKind = 1;
    private const int counterRecordKind = SqlCatalogCreation.CounterRecordKind;
    private const int indexRegistrationsKind = 3;
    private const int recordSpaceFormatKind = 4;
    private const int indexRecordKind = 5;
    private const int schemaStateRecordKind = 6;
    private const int defaultCollationRecordKind = SqlCatalogCreation.CollationRecordKind;
    private const int pendingMigrationRecordKind = 9;

    // The record-space format marker from version 4 on (#1099). Catalogs written
    // before format 4 (through 10.0.0-preview.1) load kinds 1-7 only and refuse
    // any other kind, so persisting a format-4 marker under a new kind makes those
    // engines fail the open instead of accepting the database and writing
    // format-3 index keys into it. Versions 1-3 keep the kind-4 record.
    private const int fencedRecordSpaceFormatKind = 8;
    private const int firstFencedRecordSpaceFormatVersion = 4;

    private const int schemaStateChunkSize = 3 * 1024;

    // The version of a table record's trailing extension this catalog writes; it reads
    // only the current version. Version 8 adds each constraint's ON UPDATE action;
    // version 7 adds TYPE default assignment coercion;
    // version 6 adds frozen missing values; version 5 adds identity facets; version 3 adds the dropped columns' physical ordinals (#1241),
    // version 4 (data-storage format 7, phase E3) each column's domain name.
    private const int tableExtensionVersion = 8;

    private static readonly Encoding _strictUtf8 = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly SqlStorage _storage;
    private readonly Dictionary<(string Schema, string Name), TableSlot> _tables = new(TableNameComparer.Instance);
    private readonly Dictionary<(ulong TableObjectId, string Name), IndexSlot> _indexes = new(IndexNameComparer.Instance);
    private readonly Dictionary<ulong, SqlReclamationIntent> _pendingReclamations = new();
    private readonly object _sync = new();
    private ulong _nextObjectId = 1;
    private ulong _ddlGeneration;
    private SqlCatalogSnapshot? _snapshot;
    private int _recordSpaceFormatVersion = 1;
    private Collation _defaultCollation = Collation.Binary;
    private (PageId PageId, int SlotIndex)? _counterLocation;
    private (PageId PageId, int SlotIndex)? _registrationsLocation;
    private (PageId PageId, int SlotIndex)? _formatLocation;
    private (PageId PageId, int SlotIndex)? _defaultCollationLocation;
    private List<(PageId PageId, int SlotIndex)> _schemaStateLocations = [];
    private IReadOnlyList<BTreeIndexRegistration> _registrations = Array.Empty<BTreeIndexRegistration>();
    private SqlCatalogSchemaState? _schemaState;
    private SqlCatalogPendingMigration? _pendingMigration;
    private List<(PageId PageId, int SlotIndex)> _pendingMigrationLocations = [];

    private SqlCatalog(SqlStorage storage)
    {
        _storage = storage;
    }

    /// <summary>
    /// Opens the catalog persisted in the given storage (an empty storage yields an
    /// empty catalog).
    /// </summary>
    /// <param name="storage">The dedicated catalog storage file set.</param>
    /// <returns>The catalog.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="storage"/> is null.</exception>
    /// <exception cref="SqlCatalogException">A persisted record is malformed.</exception>
    /// <exception cref="StorageOfflineException">The catalog storage is offline; reopen the database before reading or changing its metadata.</exception>
    public static SqlCatalog Open(SqlStorage storage)
    {
        ArgumentNullException.ThrowIfNull(storage);
        var catalog = new SqlCatalog(storage);
        catalog.Load();
        return catalog;
    }

    /// <summary>
    /// Opens the catalog and establishes its database default string collation.
    /// </summary>
    /// <param name="storage">The dedicated catalog storage file set.</param>
    /// <param name="defaultCollation">
    /// The database default collation, adopted only by an empty catalog. Reopening a
    /// populated catalog keeps its persisted default, so the value is fixed for the
    /// lifetime of the database.
    /// </param>
    /// <returns>The catalog.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="storage"/> or <paramref name="defaultCollation"/> is null.</exception>
    /// <exception cref="SqlCatalogException">
    /// A persisted record is malformed, or the catalog already contains tables under a different
    /// default. Their index keys are encoded through the collation they inherited, so it cannot be
    /// changed.
    /// </exception>
    /// <exception cref="StorageOfflineException">The catalog storage is offline; reopen the database before reading or changing its metadata.</exception>
    public static SqlCatalog Open(SqlStorage storage, Collation defaultCollation)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(defaultCollation);
        var catalog = new SqlCatalog(storage);
        catalog.Load();
        catalog.InitializeDefaultCollation(defaultCollation);
        return catalog;
    }

    /// <summary>
    /// Captures namespaces, tables, views, definitions, index descriptions and default collation atomically.
    /// </summary>
    /// <returns>A read-only capture unaffected by later catalog publications.</returns>
    /// <exception cref="StorageOfflineException">The catalog storage is offline; reopen the database before reading or changing its metadata.</exception>
    public SqlCatalogSnapshot CaptureSnapshot()
    {
        lock (_sync)
        {
            ThrowIfOffline();
            // Captures contain immutable definitions. Share one until a committed definition
            // or storage-owner publication invalidates it under this same catalog monitor.
            return _snapshot ??= new SqlCatalogSnapshot(_tables.Values.Select(slot => slot.Table), _indexes.Values.Select(slot => slot.Index), _defaultCollation, _functions.Values.Select(slot => slot.Value), _domains.Values.Select(slot => slot.Value), _namespaces.Values.Select(slot => slot.Value), _views.Values.Select(slot => slot.Value)) { Sequences = _sequences.Count == 0 ? [] : Sequences };
        }
    }

    // The catalog owns committed reclamation state and its retirement, as it owns pending
    // migrations and script receipts; the worker captures immutable intent incarnations.
    /// <summary>Captures permanently retired table-owner identities whose storage reclamation is pending.</summary>
    /// <returns>A read-only snapshot unaffected by later publications or completed reclamations.</returns>
    /// <remarks>Prepared TRUNCATE and CTAS owners remain internal while they can become live.
    /// Abandoned CTAS owners become pending as soon as the build exits, including a lost name race.
    /// Reclamation must wait for transaction and statement pins before freeing owner pages. Retired owners become
    /// discoverable after the TRUNCATE visibility marker ends, so the fence covers every old reader.</remarks>
    /// <exception cref="StorageOfflineException">The catalog is offline.</exception>
    public IReadOnlyList<ulong> GetPendingTableReclamations()
    {
        lock (_sync)
        {
            ThrowIfOffline();
            return Array.AsReadOnly(_pendingReclamations.Keys.Where(id =>
                !_preparedTruncateOwners.Contains(id) && !_truncateRetirementsPublishing.Contains(id)).ToArray());
        }
    }

    /// <summary>Retires a table's reclamation intent after its data and index pages have been reclaimed.</summary>
    /// <param name="objectId">The retired table identity, never a table name.</param>
    /// <param name="cancellationToken">Cancels before the retirement transaction begins.</param>
    /// <returns>A completed task after the retirement commit; an already retired or unknown identity is a no-op.</returns>
    /// <remarks>
    /// The caller must finish reclaiming the identity's data and index pages using their storage's
    /// commit durability policy before calling this method. Catalog retirement does not walk data
    /// pages or advance the DDL generation. Publication and removal of pending identities share the
    /// catalog's metadata lock; the caller must release its data transaction before retiring an intent.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="objectId"/> is zero.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested before retirement began.</exception>
    /// <exception cref="StorageOfflineException">The catalog is offline or the retirement commit is unconfirmed.</exception>
    public ValueTask CompleteTableReclamationAsync(ulong objectId, CancellationToken cancellationToken = default)
        => CompleteTableReclamationCoreAsync(objectId, null, cancellationToken);

    internal ValueTask CompleteTableReclamationAsync(ulong objectId, SqlReclamationIntent intent, CancellationToken cancellationToken)
        => CompleteTableReclamationCoreAsync(objectId, intent, cancellationToken);

    private ValueTask CompleteTableReclamationCoreAsync(ulong objectId, SqlReclamationIntent? intent, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfZero(objectId);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            ThrowIfOffline();
            cancellationToken.ThrowIfCancellationRequested();
            if (!_pendingReclamations.TryGetValue(objectId, out var location)) { return default; }
            if (_truncateRetirementsPublishing.Contains(objectId) ||
                (intent is null ? _preparedTruncateOwners.Contains(objectId) : !ReferenceEquals(intent, location))) { return default; }
            using var transaction = _storage.BeginTransaction();
            _storage.DeleteRow(transaction, location.PageId, location.SlotIndex);
            DefinitionSlot<SqlCatalogSequence>[] orphaned = _sequences.Count == 0 ? [] : _sequences.Values.Where(slot => slot.Value.OwnerTableId == objectId).ToArray();
            foreach (var sequence in orphaned) { DeleteSequence(transaction, sequence); }
            transaction.Commit();
            foreach (var sequence in orphaned) { RemoveSequence(sequence.Value); }
            _pendingReclamations.Remove(objectId);
            _truncateRetirementsPublishing.Remove(objectId);
            _preparedTruncateOwners.Remove(objectId);
            if (_truncatedTables.Remove(objectId, out var retired))
            {
                foreach (var index in retired.Before.GetIndexes(objectId)) { _truncatedIndexes.Remove(index); }
                RemoveOrderedTruncation(objectId);
            }
            return default;
        }
    }

    /// <summary>
    /// Gets the persisted default string collation, or Binary when none was configured.
    /// </summary>
    /// <remarks>
    /// Read-only by design. The default is established when the catalog is opened
    /// (<see cref="Open(SqlStorage, Collation)"/>) and never changes afterwards,
    /// because every index key on a column that inherited it is encoded through its
    /// byte transform.
    /// </remarks>
    /// <exception cref="StorageOfflineException">The catalog storage is offline; reopen the database before reading or changing its metadata.</exception>
    public Collation DefaultCollation
    {
        get
        {
            // Established before Open publishes the catalog and immutable thereafter.
            ThrowIfOffline();
            return _defaultCollation;
        }
    }

    /// <summary>
    /// Establishes the database default collation at open time. Only a catalog with no
    /// tables can adopt a different default: an existing table's index keys are encoded
    /// through the collation it inherited, so changing it would invalidate them.
    /// </summary>
    /// <param name="collation">The database default collation.</param>
    /// <exception cref="SqlCatalogException">The catalog contains tables and the default would change.</exception>
    private void InitializeDefaultCollation(Collation collation)
    {
        ArgumentNullException.ThrowIfNull(collation);
        lock (_sync)
        {
            ThrowIfOffline();
            if (collation.Id == _defaultCollation.Id)
            {
                return;
            }
            if (_tables.Count > 0)
            {
                throw new SqlCatalogException("CS05057", ErrorMessages.InitializeDefaultCollationSqlCatalog);
            }

            var writer = new DatabaseKeyWriter();
            writer.AppendInt32(defaultCollationRecordKind).AppendInt8((sbyte)collation.Id);
            using var transaction = _storage.BeginTransaction();
            var location = UpsertRecord(transaction, _defaultCollationLocation, writer.ToArray());
            CommitCatalogChange(transaction);
            _defaultCollationLocation = location;
            _defaultCollation = collation;
        }
    }

    /// <summary>
    /// Gets the canonical compiled schema most recently applied to this database,
    /// or <see langword="null"/> when no schema has been recorded.
    /// </summary>
    /// <exception cref="StorageOfflineException">The catalog storage is offline; reopen the database before reading or changing its metadata.</exception>
    public SqlCatalogSchemaState? SchemaState
    {
        get
        {
            lock (_sync)
            {
                ThrowIfOffline();
                return _schemaState;
            }
        }
    }

    /// <summary>Gets the durable deferred migration, or null when no migration is pending.</summary>
    /// <exception cref="StorageOfflineException">The catalog is offline.</exception>
    public SqlCatalogPendingMigration? PendingMigration
    {
        get
        {
            lock (_sync)
            {
                ThrowIfOffline();
                return _pendingMigration;
            }
        }
    }

    /// <summary>
    /// Gets the durable generation of committed logical catalog definitions. Schema application state
    /// records this value to detect later DDL, including changes to ad-hoc objects.
    /// </summary>
    /// <remarks>
    /// The counter and the metadata change share one transaction. Recording applied-schema
    /// state, saving an unchanged index directory or replacing storage with TRUNCATE does not advance it.
    /// </remarks>
    /// <exception cref="StorageOfflineException">The catalog storage is offline; reopen the database before reading or changing its metadata.</exception>
    public ulong DdlGeneration
    {
        get
        {
            lock (_sync)
            {
                ThrowIfOffline();
                return _ddlGeneration;
            }
        }
    }

    /// <summary>
    /// Gets every table in the catalog.
    /// </summary>
    /// <exception cref="StorageOfflineException">The catalog storage is offline; reopen the database before reading or changing its metadata.</exception>
    public IReadOnlyList<SqlCatalogTable> Tables
    {
        get
        {
            lock (_sync)
            {
                ThrowIfOffline();
                return _tables.Values.Select(slot => slot.Table).ToList();
            }
        }
    }

    /// <summary>
    /// Finds a table by schema and name (case-insensitive).
    /// </summary>
    /// <param name="schema">The schema name.</param>
    /// <param name="name">The table name.</param>
    /// <param name="table">When this method returns true, the table.</param>
    /// <returns>True when the table exists; otherwise false.</returns>
    /// <exception cref="StorageOfflineException">The catalog storage is offline; reopen the database before reading or changing its metadata.</exception>
    public bool TryGetTable(string schema, string name, out SqlCatalogTable table)
    {
        lock (_sync)
        {
            ThrowIfOffline();
            if (_tables.TryGetValue((schema, name), out var slot))
            {
                table = slot.Table;
                return true;
            }
        }

        table = null!;
        return false;
    }

    /// <summary>
    /// Creates a table. The definition's object identity is assigned by the catalog.
    /// </summary>
    /// <param name="schema">The schema name.</param>
    /// <param name="name">The table name, unique within the schema.</param>
    /// <param name="columns">The ordered column definitions.</param>
    /// <param name="primaryKeyColumns">The primary-key column names, when declared.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The created table with its assigned object identity.</returns>
    /// <exception cref="SqlCatalogException">A table with the name already exists, or the definition is invalid.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled before the write.</exception>
    /// <exception cref="StorageOfflineException">The catalog storage is offline; reopen the database before reading or changing its metadata.</exception>
    public ValueTask<SqlCatalogTable> CreateTableAsync(
        string schema,
        string name,
        IReadOnlyList<SqlCatalogColumn> columns,
        IReadOnlyList<string>? primaryKeyColumns = null,
        CancellationToken cancellationToken = default)
        => CreateTableCoreAsync(schema, name, columns, primaryKeyColumns, cancellationToken, publication: null);

    /// <summary>
    /// Creates a table. The definition's object identity is assigned by the catalog.
    /// </summary>
    /// <param name="schema">The schema name.</param>
    /// <param name="name">The table name, unique within the schema.</param>
    /// <param name="columns">The ordered column definitions.</param>
    /// <param name="primaryKeyColumns">The primary-key column names, when declared.</param>
    /// <param name="publication">The expected statement target and exact preceding receipt and catalog generation, validated before commit. Claims are derived from this mutation.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The created table with its assigned object identity.</returns>
    /// <exception cref="SqlCatalogException">A table with the name already exists, or the definition is invalid.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled before the write.</exception>
    /// <exception cref="StorageOfflineException">The catalog storage is offline; reopen the database before reading or changing its metadata.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="publication"/> is null.</exception>
    /// <exception cref="SqlCatalogException">The target or preceding state does not match, the script identity changed, or the receipt exceeds the record limit.</exception>
    public ValueTask<SqlCatalogTable> CreateTableAsync(
        string schema,
        string name,
        IReadOnlyList<SqlCatalogColumn> columns,
        IReadOnlyList<string>? primaryKeyColumns,
        SqlCatalogScriptPublication publication,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(publication);
        return CreateTableCoreAsync(schema, name, columns, primaryKeyColumns, cancellationToken, publication);
    }

    private ValueTask<SqlCatalogTable> CreateTableCoreAsync(
        string schema,
        string name,
        IReadOnlyList<SqlCatalogColumn> columns,
        IReadOnlyList<string>? primaryKeyColumns,
        CancellationToken cancellationToken,
        SqlCatalogScriptPublication? publication)
        => CreateTableOwnedCoreAsync(schema, name, columns, primaryKeyColumns,
            DatabaseObjectOwner.Adhoc, owningSchema: null, constraints: null, publication, cancellationToken);

    /// <summary>
    /// Creates a table with its ownership and constraints in one write, for the catalog's tests;
    /// engine provisioning uses the reserve, build and publish lifecycle
    /// (<see cref="ReserveTableAsync"/>, <see cref="PublishTableAsync"/>).
    /// </summary>
    internal ValueTask<SqlCatalogTable> CreateTableAsync(
        string schema,
        string name,
        IReadOnlyList<SqlCatalogColumn> columns,
        IReadOnlyList<string>? primaryKeyColumns,
        DatabaseObjectOwner owner,
        string? owningSchema,
        CancellationToken cancellationToken,
        IReadOnlyList<SqlCatalogConstraint>? constraints = null)
        => CreateTableOwnedCoreAsync(schema, name, columns, primaryKeyColumns, owner, owningSchema,
            constraints, publication: null, cancellationToken);

    private ValueTask<SqlCatalogTable> CreateTableOwnedCoreAsync(
        string schema,
        string name,
        IReadOnlyList<SqlCatalogColumn> columns,
        IReadOnlyList<string>? primaryKeyColumns,
        DatabaseObjectOwner owner,
        string? owningSchema,
        IReadOnlyList<SqlCatalogConstraint>? constraints,
        SqlCatalogScriptPublication? publication,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {
            ThrowIfOffline();
            if (RelationExists(schema, name))
            {
                if (_sequences.ContainsKey((schema, name))) { throw RelationNameCollision(schema, name); }
                throw new SqlCatalogException("CS03049", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.RenameTableCollision, schema, name));
            }

            ValidateColumns(schema, name, columns, primaryKeyColumns);

            var table = new SqlCatalogTable(_nextObjectId, schema, name, columns, primaryKeyColumns, owner, owningSchema, constraints);
            ValidateConstraints(table);

            using (var transaction = _storage.BeginTransaction())
            {
                var location = _storage.InsertRow(transaction, EncodeTable(table));
                ulong next = checked(_nextObjectId + 1);
                var owned = StageOwnedSequences(transaction, null, table, ref next);
                CommitCatalogChange(transaction, next, publication, actualObject: SqlObjectIdentity.Table(schema, name), createsObject: true);
                owned();
                _tables[(schema, name)] = new TableSlot(table, location);
            }

            return new ValueTask<SqlCatalogTable>(table);
        }
    }

    /// <summary>
    /// Drops a table.
    /// </summary>
    /// <param name="schema">The schema name.</param>
    /// <param name="name">The table name.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>A task that completes once the drop is durable.</returns>
    /// <exception cref="SqlCatalogException">The table does not exist, or another table references it.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled before the write.</exception>
    /// <exception cref="StorageOfflineException">The catalog storage is offline; reopen the database before reading or changing its metadata.</exception>
    public ValueTask DropTableAsync(
        string schema,
        string name,
        CancellationToken cancellationToken = default)
        => DropTableCoreAsync(schema, name, cancellationToken, publication: null);

    /// <summary>
    /// Drops a table or certifies its absence for one declared script statement.
    /// </summary>
    /// <param name="schema">The schema name.</param>
    /// <param name="name">The table name.</param>
    /// <param name="publication">The expected statement target and exact preceding receipt and catalog generation, validated before commit. Claims are derived from this mutation.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>A task that completes once the drop or absence receipt is durable. An absence receipt does not advance DDL generation.</returns>
    /// <exception cref="SqlCatalogException">Another table references it.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled before the write.</exception>
    /// <exception cref="StorageOfflineException">The catalog storage is offline; reopen the database before reading or changing its metadata.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="publication"/> is null.</exception>
    /// <exception cref="SqlCatalogException">The target or preceding state does not match, the script identity changed, or the receipt exceeds the record limit.</exception>
    public ValueTask DropTableAsync(
        string schema,
        string name,
        SqlCatalogScriptPublication publication,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(publication);
        return DropTableCoreAsync(schema, name, cancellationToken, publication);
    }

    private ValueTask DropTableCoreAsync(
        string schema,
        string name,
        CancellationToken cancellationToken,
        SqlCatalogScriptPublication? publication)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {
            ThrowIfOffline();
            if (!_tables.TryGetValue((schema, name), out var slot))
            {
                if (publication is not null)
                {
                    CommitScriptAbsence(publication, SqlObjectIdentity.Table(schema, name));
                    return default;
                }
                throw new SqlCatalogException("CS01051", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.TableMissing, schema, name));
            }

            EnsureNoViewReads(slot.Table);
            foreach (var dependent in _tables.Values)
            {
                if (dependent.Table.ObjectId != slot.Table.ObjectId && dependent.Table.Constraints.Any(constraint =>
                    constraint.Kind == SqlCatalogConstraintKind.Reference &&
                    string.Equals(constraint.ReferencedSchema, schema, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(constraint.ReferencedTable, name, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new SqlCatalogException("CS03056", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.CatalogDropTableCoreAsyncSqlCatalog, schema, name, dependent.Table.Schema, dependent.Table.Name));
                }
            }

            // The table's index descriptions and registrations fall with it, in the
            // same self-committing transaction — a dropped table must not leave a
            // description promising an index, nor a registration re-attaching one.
            var droppedIndexes = new List<IndexSlot>();
            foreach (var indexSlot in _indexes.Values)
            {
                if (indexSlot.Index.TableObjectId == slot.Table.ObjectId)
                {
                    droppedIndexes.Add(indexSlot);
                }
            }

            var remainingRegistrations = new List<BTreeIndexRegistration>();
            foreach (var registration in _registrations)
            {
                if (registration.ObjectId != slot.Table.ObjectId)
                {
                    remainingRegistrations.Add(registration);
                }
            }

            using (var transaction = _storage.BeginTransaction())
            {
                // The intent and unpublication share this commit. Each retired identity owns
                // its intent page, so maintenance never edits a live metadata page. The worker
                // retires this record only after the data-file frees are durable.
                var reclamationLocation = _storage.InsertRow(transaction, slot.Table.ObjectId,
                    SqlReclamationRecord.Encode(slot.Table.ObjectId, schema, name));
                _storage.DeleteRow(transaction, slot.Location.PageId, slot.Location.SlotIndex);
                ulong next = _nextObjectId;
                var owned = StageOwnedSequences(transaction, slot.Table, null, ref next);

                foreach (var indexSlot in droppedIndexes)
                {
                    _storage.DeleteRow(transaction, indexSlot.Location.PageId, indexSlot.Location.SlotIndex);
                }

                var registrationsLocation = _registrationsLocation;
                if (droppedIndexes.Count > 0)
                {
                    registrationsLocation = UpsertRecord(transaction, registrationsLocation, EncodeRegistrations(remainingRegistrations));
                }

                CommitCatalogChange(transaction, publication: publication, actualObject: SqlObjectIdentity.Table(schema, name));
                owned();
                _registrationsLocation = registrationsLocation;
                _pendingReclamations.Add(slot.Table.ObjectId, new SqlReclamationIntent(reclamationLocation.PageId, reclamationLocation.SlotIndex));
            }

            _tables.Remove((schema, name));

            foreach (var indexSlot in droppedIndexes)
            {
                _indexes.Remove((indexSlot.Index.TableObjectId, indexSlot.Index.Name));
            }

            if (droppedIndexes.Count > 0)
            {
                _registrations = remainingRegistrations;
            }

            return default;
        }
    }

    /// <summary>
    /// Adds a column to a table. The column takes the next physical ordinal, after every
    /// live and dropped one (<see cref="SqlCatalogTable.PhysicalColumnCount"/>), so versions
    /// stored before the addition read it from their missing tail.
    /// </summary>
    /// <param name="schema">The schema name.</param>
    /// <param name="name">The table name.</param>
    /// <param name="column">The column to add.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The updated table description.</returns>
    /// <exception cref="SqlCatalogException">
    /// The table does not exist or already has the column, or the definition no longer fits
    /// one catalog record.
    /// </exception>
    /// <exception cref="ArgumentNullException"><paramref name="column"/> is null.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled before the write.</exception>
    /// <exception cref="StorageOfflineException">The catalog storage is offline; reopen the database before reading or changing its metadata.</exception>
    public ValueTask<SqlCatalogTable> AddColumnAsync(
        string schema,
        string name,
        SqlCatalogColumn column,
        CancellationToken cancellationToken = default)
        => AddColumnCoreAsync(schema, name, column, cancellationToken, publication: null);

    /// <summary>
    /// Adds a column to a table. The column takes the next physical ordinal, after every
    /// live and dropped one (<see cref="SqlCatalogTable.PhysicalColumnCount"/>), so versions
    /// stored before the addition read it from their missing tail.
    /// </summary>
    /// <param name="schema">The schema name.</param>
    /// <param name="name">The table name.</param>
    /// <param name="column">The column to add.</param>
    /// <param name="publication">The expected statement target and exact preceding receipt and catalog generation, validated before commit. Claims are derived from this mutation.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The updated table description.</returns>
    /// <exception cref="SqlCatalogException">
    /// The table does not exist or already has the column, or the definition no longer fits
    /// one catalog record.
    /// </exception>
    /// <exception cref="ArgumentNullException"><paramref name="column"/> is null.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled before the write.</exception>
    /// <exception cref="StorageOfflineException">The catalog storage is offline; reopen the database before reading or changing its metadata.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="publication"/> is null.</exception>
    /// <exception cref="SqlCatalogException">The target or preceding state does not match, the script identity changed, or the receipt exceeds the record limit.</exception>
    public ValueTask<SqlCatalogTable> AddColumnAsync(
        string schema,
        string name,
        SqlCatalogColumn column,
        SqlCatalogScriptPublication publication,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(publication);
        return AddColumnCoreAsync(schema, name, column, cancellationToken, publication);
    }

    private ValueTask<SqlCatalogTable> AddColumnCoreAsync(
        string schema,
        string name,
        SqlCatalogColumn column,
        CancellationToken cancellationToken,
        SqlCatalogScriptPublication? publication)
    {
        ArgumentNullException.ThrowIfNull(column);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {
            ThrowIfOffline();
            var slot = GetSlot(schema, name);

            if (slot.Table.FindColumn(column.Name) is not null)
            {
                throw new SqlCatalogException("CS03061", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.CatalogAddColumnCoreAsyncSqlCatalog, schema, name, column.Name));
            }

            // The new column takes the next physical ordinal, after every live and dropped
            // one (PostgreSQL: relnatts + 1, src/backend/commands/tablecmds.c:7445-7446), so
            // a column re-added under a dropped column's name never reads that column's values.
            var columns = slot.Table.Columns.Append(column).ToList();
            var updated = new SqlCatalogTable(slot.Table.ObjectId, schema, name, columns, slot.Table.PrimaryKeyColumns,
                slot.Table.Owner, slot.Table.OwningSchema, slot.Table.Constraints, slot.Table.DroppedColumnOrdinals);
            ReplaceTable(slot, updated, publication);
            return new ValueTask<SqlCatalogTable>(updated);
        }
    }

    /// <summary>
    /// Drops a column from a table: the column leaves <see cref="SqlCatalogTable.Columns"/>
    /// and its physical ordinal joins <see cref="SqlCatalogTable.DroppedColumnOrdinals"/>,
    /// never to be reused. This is the whole of the drop: stored rows keep the column's
    /// component, which every read skips.
    /// </summary>
    /// <param name="schema">The schema name.</param>
    /// <param name="name">The table name.</param>
    /// <param name="columnName">The column to drop.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The updated table description.</returns>
    /// <exception cref="SqlCatalogException">
    /// The table or column does not exist; the column is part of the primary key, a
    /// constraint or an unconditional index; it is the table's last column; or the definition, which keeps
    /// every dropped column's physical ordinal, no longer fits one catalog record.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled before the write.</exception>
    /// <exception cref="StorageOfflineException">The catalog storage is offline; reopen the database before reading or changing its metadata.</exception>
    public ValueTask<SqlCatalogTable> DropColumnAsync(
        string schema,
        string name,
        string columnName,
        CancellationToken cancellationToken = default)
        => DropColumnCoreAsync(schema, name, columnName, cancellationToken, publication: null);

    /// <summary>
    /// Drops a column from a table: the column leaves <see cref="SqlCatalogTable.Columns"/>
    /// and its physical ordinal joins <see cref="SqlCatalogTable.DroppedColumnOrdinals"/>,
    /// never to be reused. This is the whole of the drop: stored rows keep the column's
    /// component, which every read skips.
    /// </summary>
    /// <param name="schema">The schema name.</param>
    /// <param name="name">The table name.</param>
    /// <param name="columnName">The column to drop.</param>
    /// <param name="publication">The expected statement target and exact preceding receipt and catalog generation, validated before commit. Claims are derived from this mutation.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The updated table description.</returns>
    /// <exception cref="SqlCatalogException">
    /// The table or column does not exist; the column is part of the primary key, a
    /// constraint or an unconditional index; it is the table's last column; or the definition, which keeps
    /// every dropped column's physical ordinal, no longer fits one catalog record.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled before the write.</exception>
    /// <exception cref="StorageOfflineException">The catalog storage is offline; reopen the database before reading or changing its metadata.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="publication"/> is null.</exception>
    /// <exception cref="SqlCatalogException">The target or preceding state does not match, the script identity changed, or the receipt exceeds the record limit.</exception>
    public ValueTask<SqlCatalogTable> DropColumnAsync(
        string schema,
        string name,
        string columnName,
        SqlCatalogScriptPublication publication,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(publication);
        return DropColumnCoreAsync(schema, name, columnName, cancellationToken, publication);
    }

    private ValueTask<SqlCatalogTable> DropColumnCoreAsync(
        string schema,
        string name,
        string columnName,
        CancellationToken cancellationToken,
        SqlCatalogScriptPublication? publication)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {
            ThrowIfOffline();
            var slot = GetSlot(schema, name);
            EnsureNoViewReads(slot.Table, columnName);
            int ordinal = -1;
            for (int index = 0; index < slot.Table.Columns.Count; index++)
            {
                if (string.Equals(slot.Table.Columns[index].Name, columnName, StringComparison.OrdinalIgnoreCase))
                {
                    ordinal = index;
                    break;
                }
            }

            if (ordinal < 0)
            {
                throw new SqlCatalogException("CS01015", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.ColumnNameMissing, schema, name, columnName));
            }

            if (slot.Table.PrimaryKeyColumns.Any(pk => string.Equals(pk, columnName, StringComparison.OrdinalIgnoreCase)))
            {
                throw new SqlCatalogException("CS03056", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.CatalogDropColumnCoreAsyncSqlCatalog, columnName, schema, name));
            }

            foreach (var dependent in _tables.Values)
            {
                foreach (var constraint in dependent.Table.Constraints)
                {
                    bool local = dependent.Table.ObjectId == slot.Table.ObjectId &&
                        constraint.Columns.Any(column => string.Equals(column, columnName, StringComparison.OrdinalIgnoreCase));
                    bool referenced = constraint.Kind == SqlCatalogConstraintKind.Reference &&
                        string.Equals(constraint.ReferencedSchema, schema, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(constraint.ReferencedTable, name, StringComparison.OrdinalIgnoreCase) &&
                        constraint.ReferencedColumns.Any(column => string.Equals(column, columnName, StringComparison.OrdinalIgnoreCase));
                    if (local || referenced)
                    {
                        throw new SqlCatalogException("CS03056", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.CatalogDropColumnCoreAsyncSqlCatalog2, columnName, constraint.Name));
                    }
                }
            }

            // Preserve the existing unconditional-index key dependency refusal.
            // Dependent partial indexes are retired atomically with the column below.
            foreach (var indexSlot in _indexes.Values)
            {
                if (indexSlot.Index.TableObjectId == slot.Table.ObjectId && indexSlot.Index.Predicate is null &&
                    indexSlot.Index.ColumnNames.Any(column => string.Equals(column, columnName, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new SqlCatalogException("CS03056",
                        string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.CatalogDropColumnCoreAsyncSqlCatalog3, columnName, indexSlot.Index.Name, schema, name));
                }
            }

            if (slot.Table.Columns.Count == 1)
            {
                throw new SqlCatalogException("CS03053", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.CatalogDropColumnCoreAsyncSqlCatalog4, schema, name));
            }

            // The whole of DROP COLUMN: the column leaves the live list and its physical
            // ordinal is marked dropped, in one catalog record. No row is rewritten — every
            // stored version keeps the dropped component, which every read skips, and later
            // writes store NULL there (or nothing, behind the last live column). PostgreSQL's
            // RemoveAttributeById, "the guts of ALTER
            // TABLE DROP COLUMN", likewise only marks the attribute (attisdropped,
            // src/backend/catalog/heap.c:1692-1732) and leaves the tuples alone.
            var columns = slot.Table.Columns.Where((_, index) => index != ordinal).ToList();
            int[] dropped = [.. slot.Table.DroppedColumnOrdinals, slot.Table.GetPhysicalOrdinal(ordinal)];
            var updated = new SqlCatalogTable(slot.Table.ObjectId, schema, name, columns, slot.Table.PrimaryKeyColumns,
                slot.Table.Owner, slot.Table.OwningSchema, slot.Table.Constraints, dropped);
            var droppedIndexes = _indexes.Values.Where(index => index.Index.TableObjectId == slot.Table.ObjectId &&
                index.Index.Predicate is not null && (index.Index.PredicateColumns.Contains(columnName, StringComparer.OrdinalIgnoreCase) ||
                index.Index.ColumnNames.Contains(columnName, StringComparer.OrdinalIgnoreCase))).ToArray();
            ReplaceTable(slot, updated, publication, droppedIndexes);
            return new ValueTask<SqlCatalogTable>(updated);
        }
    }

    /// <summary>
    /// Gets the secondary indexes declared on a table.
    /// </summary>
    /// <param name="tableObjectId">The table's object identity.</param>
    /// <returns>The table's index descriptions; empty when it has none.</returns>
    /// <exception cref="StorageOfflineException">The catalog storage is offline; reopen the database before reading or changing its metadata.</exception>
    public IReadOnlyList<SqlCatalogIndex> GetIndexes(ulong tableObjectId)
    {
        lock (_sync)
        {
            ThrowIfOffline();
            var result = new List<SqlCatalogIndex>();

            // The persisted registration vector appends creations and retains survivors'
            // order, unlike dictionary slots reused after DROP. Keep this order through
            // reopen and storage rewrites, with a table-declared primary key first.
            for (int ordinal = 0; ordinal < _registrations.Count; ordinal++)
            {
                var registration = _registrations[ordinal];
                if (registration.ObjectId == tableObjectId &&
                    _indexes.TryGetValue((tableObjectId, registration.Definition.Name), out var registered))
                { result.Add(registered.Index); }
            }
            foreach (var slot in _indexes.Values)
            {
                // Metadata-only catalog callers can publish descriptions before trees.
                if (slot.Index.TableObjectId == tableObjectId && !result.Contains(slot.Index))
                {
                    result.Add(slot.Index);
                }
            }

            return result;
        }
    }

    /// <summary>
    /// Finds an index by owning table and name (case-insensitive).
    /// </summary>
    /// <param name="tableObjectId">The table's object identity.</param>
    /// <param name="name">The index name.</param>
    /// <param name="index">When this method returns true, the index description.</param>
    /// <returns>True when the index exists; otherwise false.</returns>
    /// <exception cref="StorageOfflineException">The catalog storage is offline; reopen the database before reading or changing its metadata.</exception>
    public bool TryGetIndex(ulong tableObjectId, string name, out SqlCatalogIndex index)
    {
        lock (_sync)
        {
            ThrowIfOffline();
            if (_indexes.TryGetValue((tableObjectId, name), out var slot))
            {
                index = slot.Index;
                return true;
            }
        }

        index = null!;
        return false;
    }

    /// <summary>
    /// Creates an index description and persists the given physical registrations
    /// in the same self-committing catalog transaction — the schema description and
    /// the tree registration must never tear apart across a crash (a registration
    /// without a description is an unused tree; a description without a
    /// registration would promise uniqueness no tree enforces).
    /// </summary>
    /// <param name="index">The index description to persist.</param>
    /// <param name="registrations">A physical registration snapshot containing exactly one registration for the created index, matching its table, name (case-insensitively), and uniqueness, with B-tree kind and a positive root page id. Only that registration is merged into the current directory.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The created index description.</returns>
    /// <exception cref="SqlCatalogException">The table or a key column does not exist, the index name already exists on the table, or the required matching physical registration is missing, duplicated, or invalid.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="index"/> or <paramref name="registrations"/> is null.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled before the write.</exception>
    /// <exception cref="StorageOfflineException">The catalog storage is offline; reopen the database before reading or changing its metadata.</exception>
    public ValueTask<SqlCatalogIndex> CreateIndexAsync(
        SqlCatalogIndex index,
        IReadOnlyList<BTreeIndexRegistration> registrations,
        CancellationToken cancellationToken = default)
        => CreateIndexCoreAsync(index, registrations, cancellationToken, publication: null);

    /// <summary>
    /// Creates an index description and persists the given physical registrations
    /// in the same self-committing catalog transaction — the schema description and
    /// the tree registration must never tear apart across a crash (a registration
    /// without a description is an unused tree; a description without a
    /// registration would promise uniqueness no tree enforces).
    /// </summary>
    /// <param name="index">The index description to persist.</param>
    /// <param name="registrations">A physical registration snapshot containing exactly one registration for the created index, matching its table, name (case-insensitively), and uniqueness, with B-tree kind and a positive root page id. Only that registration is merged into the current directory.</param>
    /// <param name="publication">The expected statement target and exact preceding receipt and catalog generation, validated before commit. Claims are derived from this mutation.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The created index description.</returns>
    /// <exception cref="SqlCatalogException">The table or a key column does not exist, the index name already exists on the table, or the required matching physical registration is missing, duplicated, or invalid.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="index"/> or <paramref name="registrations"/> is null.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled before the write.</exception>
    /// <exception cref="StorageOfflineException">The catalog storage is offline; reopen the database before reading or changing its metadata.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="publication"/> is null.</exception>
    /// <exception cref="SqlCatalogException">The target or preceding state does not match, the script identity changed, or the receipt exceeds the record limit.</exception>
    public ValueTask<SqlCatalogIndex> CreateIndexAsync(
        SqlCatalogIndex index,
        IReadOnlyList<BTreeIndexRegistration> registrations,
        SqlCatalogScriptPublication publication,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(publication);
        return CreateIndexCoreAsync(index, registrations, cancellationToken, publication);
    }

    private ValueTask<SqlCatalogIndex> CreateIndexCoreAsync(
        SqlCatalogIndex index,
        IReadOnlyList<BTreeIndexRegistration> registrations,
        CancellationToken cancellationToken,
        SqlCatalogScriptPublication? publication)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(registrations);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {
            ThrowIfOffline();
            var table = FindTableByObjectId(index.TableObjectId)
                ?? throw new SqlCatalogException("CS01051", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.CatalogCreateIndexCoreAsyncSqlCatalog, index.TableObjectId));

            if (_indexes.ContainsKey((index.TableObjectId, index.Name)))
            {
                throw new SqlCatalogException("CS03049", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.ExecutionIndexExists, index.Name, table.Schema, table.Name));
            }
            if (table.Constraints.Any(constraint => string.Equals(constraint.Name, index.Name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new SqlCatalogException("CS03050", string.Format(System.Globalization.CultureInfo.CurrentCulture,
                    ErrorMessages.ConstraintNameExists, index.Name, table.Name));
            }

            foreach (string column in index.ColumnNames.Concat(index.PredicateColumns))
            {
                if (table.FindColumn(column) is null)
                {
                    throw new SqlCatalogException("CS01015", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.CatalogCreateIndexCoreAsyncSqlCatalog2, index.Name, table.Schema, table.Name, column));
                }
            }

            var mergedRegistrations = MergePublishedRegistrations(registrations, [index]);
            using (var transaction = _storage.BeginTransaction())
            {
                var location = _storage.InsertRow(transaction, EncodeIndex(index));
                var registrationsLocation = UpsertRecord(transaction, _registrationsLocation, EncodeRegistrations(mergedRegistrations));
                CommitCatalogChange(transaction, publication: publication, actualObject: SqlObjectIdentity.Index(table.Schema, table.Name, index.Name), createsObject: true);
                _registrationsLocation = registrationsLocation;
                _indexes[(index.TableObjectId, index.Name)] = new IndexSlot(index, location);
            }

            _registrations = mergedRegistrations;
            return new ValueTask<SqlCatalogIndex>(index);
        }
    }

    /// <summary>
    /// Drops an index description and removes its current physical registration in
    /// the same self-committing catalog transaction (see
    /// <see cref="CreateIndexAsync"/> for why the two writes are atomic).
    /// </summary>
    /// <param name="tableObjectId">The table's object identity.</param>
    /// <param name="name">The index name.</param>
    /// <param name="registrations">The caller's remaining registration snapshot. Unrelated registrations are retained from the current directory instead of this possibly stale snapshot.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>A task that completes once the drop is durable.</returns>
    /// <exception cref="SqlCatalogException">The index does not exist.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="registrations"/> is null.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled before the write.</exception>
    /// <exception cref="StorageOfflineException">The catalog storage is offline; reopen the database before reading or changing its metadata.</exception>
    public ValueTask DropIndexAsync(
        ulong tableObjectId,
        string name,
        IReadOnlyList<BTreeIndexRegistration> registrations,
        CancellationToken cancellationToken = default)
        => DropIndexCoreAsync(tableObjectId, name, registrations, cancellationToken, publication: null);

    /// <summary>
    /// Drops an index description and removes its current physical registration in
    /// the same self-committing catalog transaction (see
    /// <see cref="CreateIndexAsync"/> for why the two writes are atomic), or certifies
    /// the index's absence on an existing table for one declared script statement.
    /// </summary>
    /// <param name="tableObjectId">The table's object identity.</param>
    /// <param name="name">The index name.</param>
    /// <param name="registrations">The caller's remaining registration snapshot. Unrelated registrations are retained from the current directory instead of this possibly stale snapshot.</param>
    /// <param name="publication">The expected statement target and exact preceding receipt and catalog generation, validated before commit. Claims are derived from this mutation.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>A task that completes once the drop or absence receipt is durable. An absence receipt does not advance DDL generation.</returns>
    /// <exception cref="SqlCatalogException">The table does not exist.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="registrations"/> is null.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled before the write.</exception>
    /// <exception cref="StorageOfflineException">The catalog storage is offline; reopen the database before reading or changing its metadata.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="publication"/> is null.</exception>
    /// <exception cref="SqlCatalogException">The target or preceding state does not match, the script identity changed, or the receipt exceeds the record limit.</exception>
    public ValueTask DropIndexAsync(
        ulong tableObjectId,
        string name,
        IReadOnlyList<BTreeIndexRegistration> registrations,
        SqlCatalogScriptPublication publication,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(publication);
        return DropIndexCoreAsync(tableObjectId, name, registrations, cancellationToken, publication);
    }

    private ValueTask DropIndexCoreAsync(
        ulong tableObjectId,
        string name,
        IReadOnlyList<BTreeIndexRegistration> registrations,
        CancellationToken cancellationToken,
        SqlCatalogScriptPublication? publication)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {
            ThrowIfOffline();
            if (!_indexes.TryGetValue((tableObjectId, name), out var slot))
            {
                if (publication is not null && FindTableByObjectId(tableObjectId) is { } existingTable)
                {
                    CommitScriptAbsence(publication, SqlObjectIdentity.Index(existingTable.Schema, existingTable.Name, name));
                    return default;
                }
                throw new SqlCatalogException("CS01052", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.CatalogDropIndexCoreAsyncSqlCatalog, name, tableObjectId));
            }

            var table = FindTableByObjectId(tableObjectId)!;
            var remainingRegistrations = _registrations.Where(registration =>
                registration.ObjectId != tableObjectId ||
                !string.Equals(registration.Definition.Name, name, StringComparison.OrdinalIgnoreCase)).ToArray();
            using (var transaction = _storage.BeginTransaction())
            {
                _storage.DeleteRow(transaction, slot.Location.PageId, slot.Location.SlotIndex);
                var registrationsLocation = UpsertRecord(transaction, _registrationsLocation, EncodeRegistrations(remainingRegistrations));
                CommitCatalogChange(transaction, publication: publication, actualObject: SqlObjectIdentity.Index(table.Schema, table.Name, slot.Index.Name));
                _registrationsLocation = registrationsLocation;
            }

            _indexes.Remove((tableObjectId, name));
            _registrations = remainingRegistrations;
            return default;
        }
    }

    // Called only under _sync. Publishers can finish trees and capture snapshots
    // independently, so only their own new descriptions may change the directory.
    private BTreeIndexRegistration[] MergePublishedRegistrations(
        IReadOnlyList<BTreeIndexRegistration> registrations, IReadOnlyList<SqlCatalogIndex> indexes)
    {
        var merged = _registrations.ToDictionary(registration =>
            (registration.ObjectId, registration.Definition.Name), IndexNameComparer.Instance);
        foreach (var index in indexes)
        {
            var matches = registrations.Where(registration => registration.ObjectId == index.TableObjectId &&
                string.Equals(registration.Definition.Name, index.Name, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length != 1 || matches[0].Definition.IsUnique != index.IsUnique ||
                matches[0].Definition.Kind != IndexKind.BTree || matches[0].RootPageId <= 0)
            {
                throw new SqlCatalogException("CS05060",
                    string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.MergePublishedRegistrationsSqlCatalog, index.Name));
            }
            merged[(index.TableObjectId, index.Name)] = matches[0];
        }
        return merged.Values.ToArray();
    }

    /// <summary>
    /// Loads the persisted index registrations.
    /// </summary>
    /// <returns>The stored registrations, empty when none were saved.</returns>
    /// <exception cref="StorageOfflineException">The catalog storage is offline; reopen the database before reading or changing its metadata.</exception>
    public IReadOnlyList<BTreeIndexRegistration> GetIndexRegistrations()
    {
        lock (_sync)
        {
            ThrowIfOffline();
            return _registrations;
        }
    }

    /// <summary>
    /// Gets the record-space format version of the database's data storage (its
    /// rows and the index trees that share its file set): 1 = the pre-MVCC
    /// unstamped row layout (the value reported when no marker is persisted),
    /// 2 = MVCC-stamped records, 3 = per-object page chains, 4 = index keys that
    /// encode TIMESTAMP and TIMESTAMPTZ values by their SQL identity. The catalog
    /// is the marker's home because rows and keys are not self-describing across
    /// format changes — the engine reads this at open and refuses a data storage
    /// on any version but its own (it has no upgrade path).
    /// </summary>
    /// <exception cref="StorageOfflineException">The catalog storage is offline; reopen the database before reading or changing its metadata.</exception>
    public int RecordSpaceFormatVersion
    {
        get
        {
            lock (_sync)
            {
                ThrowIfOffline();
                return _recordSpaceFormatVersion;
            }
        }
    }

    /// <summary>
    /// Persists the record-space format version. Self-committing, like every
    /// catalog write; called by the engine at database creation, when the data
    /// storage is born on the engine's format. From version 4 on, the marker is
    /// stored in a form that catalogs written before version 4 refuse to load, so
    /// an older engine fails the open instead of writing older-format data into
    /// the database.
    /// </summary>
    /// <param name="version">The format version to persist.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>A task that completes once the marker is durable.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="version"/> is zero or negative.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled before the write.</exception>
    /// <exception cref="StorageOfflineException">The catalog storage is offline; reopen the database before reading or changing its metadata.</exception>
    public ValueTask SetRecordSpaceFormatVersionAsync(int version, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(version);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {
            ThrowIfOffline();
            if (_formatLocation is not null && version == _recordSpaceFormatVersion)
            {
                return default;
            }

            int kind = version >= firstFencedRecordSpaceFormatVersion
                ? fencedRecordSpaceFormatKind
                : recordSpaceFormatKind;
            var writer = new DatabaseKeyWriter();
            writer.AppendInt32(kind).AppendInt32(version);
            byte[] record = writer.ToArray();

            using (var transaction = _storage.BeginTransaction())
            {
                var formatLocation = UpsertRecord(transaction, _formatLocation, record);
                CommitCatalogChange(transaction);
                _formatLocation = formatLocation;
            }

            _recordSpaceFormatVersion = version;
            return default;
        }
    }

    /// <summary>
    /// Persists the canonical compiled schema most recently applied to this database.
    /// The state is replaced atomically in one self-committing catalog transaction.
    /// </summary>
    /// <param name="state">The applied schema state to persist.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>A task that completes once the state is durable.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="state"/> is null.</exception>
    /// <exception cref="SqlCatalogException">The state's generation differs from the current catalog generation.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled before the write.</exception>
    /// <exception cref="StorageOfflineException">The catalog storage is offline; reopen the database before reading or changing its metadata.</exception>
    public ValueTask SaveSchemaStateAsync(SqlCatalogSchemaState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        cancellationToken.ThrowIfCancellationRequested();

        return SaveSchemaDocument(state, pending: null, cancellationToken);
    }

    /// <summary>Atomically records a migration intent before its worker may execute.</summary>
    /// <param name="migration">The target schema and deferred step order.</param>
    /// <param name="cancellationToken">Observed before writing the intent.</param>
    /// <returns>A task that completes when the intent is durable.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="migration"/> is null.</exception>
    /// <exception cref="OperationCanceledException">The token was canceled.</exception>
    /// <exception cref="StorageOfflineException">The catalog is offline.</exception>
    public ValueTask SavePendingMigrationAsync(SqlCatalogPendingMigration migration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(migration);
        cancellationToken.ThrowIfCancellationRequested();
        string document = JsonSerializer.Serialize(migration, SqlCatalogJsonContext.Default.SqlCatalogPendingMigration);
        return SaveSchemaDocument(new SqlCatalogSchemaState(migration.ContentHash, document, DdlGeneration), migration, cancellationToken);
    }

    private ValueTask SaveSchemaDocument(SqlCatalogSchemaState state, SqlCatalogPendingMigration? pending, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        byte[] document = _strictUtf8.GetBytes(state.CanonicalDocument);
        int chunkCount = document.Length == 0
            ? 1
            : ((document.Length - 1) / schemaStateChunkSize) + 1;
        var records = new List<byte[]>(chunkCount);

        for (int index = 0; index < chunkCount; index++)
        {
            int offset = index * schemaStateChunkSize;
            int length = Math.Min(schemaStateChunkSize, document.Length - offset);
            records.Add(EncodeSchemaStateChunk(
                state.ContentHash,
                state.Generation,
                index,
                chunkCount,
                document.AsSpan(offset, length), pending is null ? schemaStateRecordKind : pendingMigrationRecordKind));
        }

        lock (_sync)
        {
            ThrowIfOffline();
            if (state.Generation != _ddlGeneration)
            {
                throw new SqlCatalogException("CS04064", ErrorMessages.CatalogSaveSchemaDocumentSqlCatalog);
            }

            var locations = new List<(PageId PageId, int SlotIndex)>(records.Count);

            using (var transaction = _storage.BeginTransaction())
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var location in pending is null ? _schemaStateLocations : _pendingMigrationLocations)
                {
                    _storage.DeleteRow(transaction, location.PageId, location.SlotIndex);
                }

                if (pending is null)
                {
                    foreach (var location in _pendingMigrationLocations)
                    {
                        _storage.DeleteRow(transaction, location.PageId, location.SlotIndex);
                    }
                }

                foreach (byte[] record in records)
                {
                    locations.Add(_storage.InsertRow(transaction, record));
                }

                transaction.Commit();
            }

            if (pending is null)
            {
                _schemaStateLocations = locations;
                _schemaState = state;
                _pendingMigrationLocations = [];
                _pendingMigration = null;
            }
            else
            {
                _pendingMigrationLocations = locations;
                _pendingMigration = pending;
            }
            return default;
        }
    }

    // ── Persistence ────────────────────────────────────────────────────

    private void Load()
    {
        ThrowIfOffline();
        var schemaStateChunks = new List<SchemaStateChunk>();
        var pendingChunks = new List<SchemaStateChunk>();
        using var iterator = _storage.GetUnitIterator();

        while (iterator.MoveNext())
        {
            var unit = iterator.Current;
            try
            {
                var reader = new DatabaseKeyReader(unit.Data.Span);
                int kind = reader.ReadInt32();

                switch (kind)
                {
                    case viewRecordKind:
                        LoadView(reader.ReadString(out _), (unit.PageId, unit.SlotIndex));
                        if (!reader.IsAtEnd) { throw new SqlCatalogException("CS03033", ErrorMessages.ViewMetadataInvalid); }
                        break;
                    case namespaceRecordKind:
                        LoadNamespace(reader.ReadString(out _), (unit.PageId, unit.SlotIndex));
                        if (!reader.IsAtEnd) { throw new SqlCatalogException("CS06059", ErrorMessages.NamespaceRecordMalformed); }
                        break;
                    case SqlReclamationRecord.Kind:
                        var intent = SqlReclamationRecord.Decode(ref reader);
                        if (!_pendingReclamations.TryAdd(intent.ObjectId, new SqlReclamationIntent(unit.PageId, unit.SlotIndex))) { throw new System.IO.InvalidDataException(); }
                        _nextObjectId = Math.Max(_nextObjectId, intent.ObjectId + 1);
                        break;
                    case 16: // Retired table-owned identity counters are never read in format 30.
                        throw new SqlCatalogException("CS06059", ErrorMessages.IdentityCounterMalformed);
                    case sequenceRecordKind:
                    case sequenceCounterRecordKind:
                        try
                        {
                            if (kind == sequenceRecordKind) { LoadSequence(ref reader, (unit.PageId, unit.SlotIndex)); }
                            else { LoadSequenceCounter(ref reader, (unit.PageId, unit.SlotIndex)); }
                        }
                        catch (Exception failure) when (failure is SqlCatalogException or DatabaseTypeException
                            or ArgumentException or OverflowException or FormatException)
                        { throw new SqlCatalogException("CS06061", ErrorMessages.SequenceRecordMalformed, failure); }
                        break;
                    case tableRecordKind:
                        var table = DecodeTable(ref reader);
                        _tables[(table.Schema, table.Name)] = new TableSlot(table, (unit.PageId, unit.SlotIndex));

                        if (table.ObjectId >= _nextObjectId)
                        {
                            _nextObjectId = table.ObjectId + 1;
                        }

                        break;

                    case counterRecordKind:
                        ulong persisted = (ulong)reader.ReadInt64();

                        if (persisted > _nextObjectId)
                        {
                            _nextObjectId = persisted;
                        }

                        if (reader.IsAtEnd)
                        {
                            throw new SqlCatalogException("CS06060",
                                ErrorMessages.SqlCatalogLoadSqlCatalog);
                        }
                        _ddlGeneration = (ulong)reader.ReadInt64();
                        if (!reader.IsAtEnd || _counterLocation is not null)
                        {
                            throw new SqlCatalogException("CS06059", ErrorMessages.SqlCatalogLoadSqlCatalog2);
                        }

                        _counterLocation = (unit.PageId, unit.SlotIndex);
                        break;

                    case indexRegistrationsKind:
                        _registrations = DecodeRegistrations(ref reader);
                        _registrationsLocation = (unit.PageId, unit.SlotIndex);
                        break;

                    case recordSpaceFormatKind:
                    case fencedRecordSpaceFormatKind:
                        _recordSpaceFormatVersion = reader.ReadInt32();
                        _formatLocation = (unit.PageId, unit.SlotIndex);
                        break;

                    case indexRecordKind:
                        var index = DecodeIndex(ref reader);
                        _indexes[(index.TableObjectId, index.Name)] = new IndexSlot(index, (unit.PageId, unit.SlotIndex));
                        break;

                    case schemaStateRecordKind:
                    case pendingMigrationRecordKind:
                        var chunk = DecodeSchemaStateChunk(ref reader);
                        (kind == schemaStateRecordKind ? schemaStateChunks : pendingChunks).Add(new SchemaStateChunk(
                            chunk.ContentHash,
                            chunk.Generation,
                            chunk.Index,
                            chunk.Count,
                            chunk.Document,
                            (unit.PageId, unit.SlotIndex)));
                        break;

                    case defaultCollationRecordKind:
                        _defaultCollation = Collation.FromId((byte)reader.ReadInt8());
                        if (!reader.IsAtEnd)
                        {
                            throw new SqlCatalogException("CS06059", ErrorMessages.SqlCatalogLoadSqlCatalog3);
                        }
                        _defaultCollationLocation = (unit.PageId, unit.SlotIndex);
                        break;

                    case 12:
                    {
                        try
                        {
                            var receipt = JsonSerializer.Deserialize(reader.ReadString(out _), SqlCatalogJsonContext.Default.SqlCatalogScript)
                                ?? throw new SqlCatalogException("CS03201", ErrorMessages.ScriptReceiptMalformed);
                            if (!reader.IsAtEnd || !_scripts.TryAdd(receipt.Name, new DefinitionSlot<SqlCatalogScript>(receipt, (unit.PageId, unit.SlotIndex))))
                            {
                                throw new SqlCatalogException("CS03201", ErrorMessages.ScriptReceiptDuplicate);
                            }
                        }
                        catch (Exception failure) when (failure is JsonException or ArgumentException)
                        {
                            throw new SqlCatalogException("CS03201", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.ScriptReceiptReadFailed, failure.Message), failure);
                        }
                        break;
                    }
                    case 13:
                        if (_scriptState is not null)
                        {
                            throw new SqlCatalogException("CS06059", ErrorMessages.SqlCatalogLoadSqlCatalog4);
                        }
                        _scriptState = new SqlCatalogScriptState(reader.ReadString(out _), (ulong)reader.ReadInt64());
                        if (!reader.IsAtEnd)
                        {
                            throw new SqlCatalogException("CS06059", ErrorMessages.SqlCatalogLoadSqlCatalog5);
                        }
                        _scriptStateLocation = (unit.PageId, unit.SlotIndex);
                        break;
                    case functionRecordKind:
                    case domainRecordKind:
                        LoadDefinition(kind, reader.ReadString(out _), (unit.PageId, unit.SlotIndex));
                        if (!reader.IsAtEnd)
                        {
                            throw new SqlCatalogException("CS06059", ErrorMessages.SqlCatalogLoadSqlCatalog6);
                        }
                        break;

                    default:
                        throw new SqlCatalogException("CS06059", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.SqlCatalogLoadSqlCatalog7, kind));
                }
            }
            catch (Exception error) when (error is SqlCatalogException or DatabaseTypeException
                or System.IO.InvalidDataException or FormatException or ArgumentException or OverflowException or JsonException)
            {
                var corruption = new StorageCorruptionException(unit.PageId, unit.SlotIndex, error.Message, error);
                throw new SqlCatalogException(corruption.Message,
                    error is SqlCatalogException { Diagnostics.Count: > 0 } catalog ? catalog.Diagnostics :
                        [new Diagnostic { Code = "CS06059", Message = error.Message, Severity = DiagnosticSeverity.Error }], corruption);
            }
        }

        var tablesById = _tables.Values.ToDictionary(slot => slot.Table.ObjectId, slot => slot.Table);

        if (_tables.Values.Any(slot => _pendingReclamations.ContainsKey(slot.Table.ObjectId)))
        {
            throw new System.IO.InvalidDataException();
        }

        ValidateSequenceDirectory(tablesById);
        ValidateViewDirectory(tablesById);

        if (schemaStateChunks.Count > 0)
        {
            _schemaState = AssembleSchemaState(schemaStateChunks);
            _schemaStateLocations = schemaStateChunks.Select(chunk => chunk.Location).ToList();
        }

        if (pendingChunks.Count > 0)
        {
            SqlCatalogSchemaState pending = AssembleSchemaState(pendingChunks);
            try
            {
                _pendingMigration = JsonSerializer.Deserialize(pending.CanonicalDocument,
                    SqlCatalogJsonContext.Default.SqlCatalogPendingMigration)
                    ?? throw new SqlCatalogException("CS06059", ErrorMessages.SqlCatalogLoadSqlCatalog8);
                if (!string.Equals(pending.ContentHash, _pendingMigration.ContentHash, StringComparison.Ordinal))
                {
                    throw new SqlCatalogException("CS06059", ErrorMessages.SqlCatalogLoadSqlCatalog9);
                }
            }
            catch (Exception failure) when (failure is JsonException or ArgumentException)
            {
                throw new SqlCatalogException("CS06059", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.SqlCatalogLoadSqlCatalog10, failure.Message));
            }
            _pendingMigrationLocations = pendingChunks.Select(chunk => chunk.Location).ToList();
        }
    }

    private void ThrowIfOffline()
    {
        if (_storage.OfflineError is { } offline)
        {
            throw offline;
        }
    }

    private TableSlot GetSlot(string schema, string name)
    {
        if (!_tables.TryGetValue((schema, name), out var slot))
        {
            throw new SqlCatalogException("CS01051", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.TableMissing, schema, name));
        }

        return slot;
    }

    private SqlCatalogTable? FindTableByObjectId(ulong objectId)
    {
        foreach (var slot in _tables.Values)
        {
            if (slot.Table.ObjectId == objectId)
            {
                return slot.Table;
            }
        }

        return null;
    }

    private void ReplaceTable(TableSlot slot, SqlCatalogTable updated, SqlCatalogScriptPublication? publication, IReadOnlyList<IndexSlot>? droppedIndexes = null)
    {
        ValidateColumns(updated.Schema, updated.Name, updated.Columns, updated.PrimaryKeyColumns);
        byte[] record = EncodeTable(updated);

        using (var transaction = _storage.BeginTransaction())
        {
            var location = UpsertRecord(transaction, slot.Location, record);
            IReadOnlyList<BTreeIndexRegistration>? remaining = null;
            if (droppedIndexes is { Count: > 0 })
            {
                remaining = _registrations.Where(registration => !droppedIndexes.Any(index =>
                    index.Index.TableObjectId == registration.ObjectId && index.Index.Name.Equals(registration.Definition.Name, StringComparison.OrdinalIgnoreCase))).ToArray();
                foreach (var index in droppedIndexes) { _storage.DeleteRow(transaction, index.Location.PageId, index.Location.SlotIndex); }
            }
            var registrationsLocation = remaining is null ? _registrationsLocation : UpsertRecord(transaction, _registrationsLocation, EncodeRegistrations(remaining));
            ulong next = _nextObjectId;
            var owned = StageOwnedSequences(transaction, slot.Table, updated, ref next);
            CommitCatalogChange(transaction, next, publication: publication, actualObject: SqlObjectIdentity.Table(updated.Schema, updated.Name));
            owned();
            _tables[(updated.Schema, updated.Name)] = new TableSlot(updated, location);
            if (remaining is not null)
            {
                _registrations = remaining;
                _registrationsLocation = registrationsLocation;
                foreach (var index in droppedIndexes!) { _indexes.Remove((index.Index.TableObjectId, index.Index.Name)); }
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
            return _storage.InsertRow(transaction, record);
        }

        try
        {
            _storage.UpdateRow(transaction, location.Value.PageId, location.Value.SlotIndex, record);
            return location.Value;
        }
        catch (SlottedPageException)
        {
            _storage.DeleteRow(transaction, location.Value.PageId, location.Value.SlotIndex);
            return _storage.InsertRow(transaction, record);
        }
    }

    private void CommitCatalogChange(
        StorageTransaction transaction,
        ulong? nextObjectId = null,
        SqlCatalogScriptPublication? publication = null,
        bool reservationOnly = false,
        string? actualObject = null,
        bool createsObject = false,
        IReadOnlyList<string>? parameterTypes = null)
    {
        if (_ddlGeneration == ulong.MaxValue)
        {
            throw new SqlCatalogException("CS05042", ErrorMessages.CatalogCommitCatalogChangeSqlCatalog);
        }
        if (publication is not null)
        {
            ValidateScriptPublication(publication, actualObject!);
        }
        ulong generation = _ddlGeneration + 1;
        ulong identity = nextObjectId ?? _nextObjectId;
        var location = PersistCounter(transaction, identity, generation);
        DefinitionSlot<SqlCatalogScript>? script = null;
        if (publication is not null)
        {
            script = StageScript(transaction, CreateScriptReceipt(publication, generation, actualObject!, createsObject, reservationOnly, parameterTypes));
        }
        transaction.Commit();
        if (script is not null)
        {
            _scripts[script.Value.Name] = script;
        }
        _counterLocation = location;
        _nextObjectId = identity;
        Volatile.Write(ref _ddlGeneration, generation);
        _snapshot = null;
    }

    private (PageId PageId, int SlotIndex) PersistCounter(
        StorageTransaction transaction, ulong nextObjectId, ulong generation)
    {
        var writer = new DatabaseKeyWriter();
        writer.AppendInt32(counterRecordKind).AppendInt64((long)nextObjectId).AppendInt64((long)generation);
        return UpsertRecord(transaction, _counterLocation, writer.ToArray());
    }

    private static void ValidateColumns(
        string schema,
        string name,
        IReadOnlyList<SqlCatalogColumn> columns,
        IReadOnlyList<string>? primaryKeyColumns)
    {
        ArgumentNullException.ThrowIfNull(columns);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool hasIdentity = false;
        foreach (var column in columns)
        {
            if (column.Identity is { } identity)
            {
                if (column.Type.Type is not (DatabaseType.Int8 or DatabaseType.Int16 or DatabaseType.Int32 or DatabaseType.Int64))
                {
                    throw new SqlCatalogException("CS03053", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.IdentityMetadataInvalid, column.Name));
                }
                var (minimum, maximum) = SqlSequenceOptions.IntegerBounds(column.Type.Type, identity: true);
                if (hasIdentity || column.IsNullable || column.DefaultLiteral is not null || column.DomainName is not null ||
                    identity.Start < minimum || identity.Start > maximum)
                {
                    throw new SqlCatalogException("CS03053", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.IdentityMetadataInvalid, column.Name));
                }
                hasIdentity = true;
            }
            if (!seen.Add(column.Name))
            {
                throw new SqlCatalogException("CS03061", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.SqlCatalogValidateColumnsSqlCatalog, schema, name, column.Name));
            }
        }

        if (primaryKeyColumns is not null)
        {
            foreach (var pk in primaryKeyColumns)
            {
                if (!seen.Contains(pk))
                {
                    throw new SqlCatalogException("CS01015", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.SqlCatalogValidateColumnsSqlCatalog2, pk, schema, name));
                }
            }
        }
    }

    // ── Record codec (shared self-describing tuple encoding) ──────────

    /// <summary>
    /// Refuses a catalog record that cannot be stored. Every catalog record lives in one
    /// slotted-page slot, so a definition encoded past <see cref="SlottedPage.MaxRecordSize"/>
    /// can never be written. Failing here, before the storage is touched, reports the
    /// definition that outgrew a page as a catalog error rather than letting a storage
    /// failure reach the client half-way through a relocation.
    /// </summary>
    /// <param name="record">The encoded record.</param>
    /// <param name="description">What the record describes, for the message.</param>
    /// <param name="remedy">A sentence the message ends with, or null.</param>
    /// <returns><paramref name="record"/>.</returns>
    /// <exception cref="SqlCatalogException">The record exceeds the maximum record size.</exception>
    private static byte[] EnsureStorable(byte[] record, string description, string? remedy = null)
    {
        if (record.Length > SlottedPage.MaxRecordSize)
        {
            throw new SqlCatalogException("CS05042",
                string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.SqlCatalogEnsureStorableSqlCatalog, description, record.Length, SlottedPage.MaxRecordSize, (remedy is null ? string.Empty : " " + remedy)));
        }

        return record;
    }

    private static byte[] EncodeTable(SqlCatalogTable table)
    {
        byte[] record = EncodeTableRecord(table);

        // A dropped column's physical ordinal stays in the record for the life of the table
        // (#1241), so a table that has dropped many columns can refuse ADD or DROP COLUMN with
        // few live ones. Only a new table starts without them; say so.
        int dropped = table.DroppedColumnOrdinals.Count;
        return EnsureStorable(record, string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.CatalogTableSubject, table.Schema, table.Name), dropped == 0 ? null :
            string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.RetainedDroppedColumnHint, dropped));
    }

    private static byte[] EncodeTableRecord(SqlCatalogTable table)
    {
        var writer = new DatabaseKeyWriter();
        writer.AppendInt32(tableRecordKind)
              .AppendInt64((long)table.ObjectId)
              .AppendString(table.Schema, Collation.Binary)
              .AppendString(table.Name, Collation.Binary)
              .AppendInt32(table.Columns.Count);

        foreach (var column in table.Columns)
        {
            writer.AppendString(column.Name, Collation.Binary)
                  .AppendInt8((sbyte)column.Type.Type)
                  .AppendInt32(column.Type.MaxLength ?? -1)
                  .AppendInt32(column.Type.Precision ?? -1)
                  .AppendInt32(column.Type.Scale ?? -1)
                  .AppendBoolean(column.IsNullable);

            if (column.DefaultLiteral is null)
            {
                writer.AppendNull();
            }
            else
            {
                writer.AppendString(column.DefaultLiteral, Collation.Binary);
            }
        }

        writer.AppendInt32(table.PrimaryKeyColumns.Count);
        foreach (var pk in table.PrimaryKeyColumns)
        {
            writer.AppendString(pk, Collation.Binary);
        }

        AppendOwnership(ref writer, table.Owner, table.OwningSchema);
        // Format 10 retains the existing constraint/layout/domain extension and appends
        // mandatory function-call identity vectors. No earlier table-record shape is accepted
        // as a current-format record; the engine's format gate refuses older databases.
        writer.AppendInt32(tableExtensionVersion).AppendInt32(table.Constraints.Count);
        foreach (SqlCatalogConstraint constraint in table.Constraints)
        {
            writer.AppendString(constraint.Name, Collation.Binary)
                  .AppendInt8((sbyte)constraint.Kind)
                  .AppendInt32(constraint.Columns.Count);
            foreach (string column in constraint.Columns)
            {
                writer.AppendString(column, Collation.Binary);
            }
            AppendOptionalString(ref writer, constraint.ReferencedSchema);
            AppendOptionalString(ref writer, constraint.ReferencedTable);
            writer.AppendInt32(constraint.ReferencedColumns.Count);
            foreach (string column in constraint.ReferencedColumns)
            {
                writer.AppendString(column, Collation.Binary);
            }
            AppendOptionalString(ref writer, constraint.CheckExpression);
            writer.AppendInt8((sbyte)constraint.OnDelete).AppendInt8((sbyte)constraint.OnUpdate);
        }
        foreach (var column in table.Columns)
        {
            writer.AppendInt32(column.Collation?.Id ?? -1);
        }
        writer.AppendInt32(table.DroppedColumnOrdinals.Count);
        foreach (int ordinal in table.DroppedColumnOrdinals)
        {
            writer.AppendInt32(ordinal);
        }
        foreach (var column in table.Columns)
        {
            AppendOptionalString(ref writer, column.DomainName);
        }
        foreach (var column in table.Columns)
        {
            AppendFunctionCalls(ref writer, column.DefaultFunctionCalls, column.DefaultLiteral?.Length ?? 0);
        }
        foreach (var constraint in table.Constraints)
        {
            AppendFunctionCalls(ref writer, constraint.CheckFunctionCalls, constraint.CheckExpression?.Length ?? 0);
        }
        foreach (var column in table.Columns)
        {
            writer.AppendBoolean(column.Identity is not null);
            if (column.Identity is { } identity)
            {
                writer.AppendBoolean(identity.IsAlways).AppendInt64(identity.Start).AppendInt64(identity.Increment);
            }
        }
        foreach (var column in table.Columns)
        {
            AppendOptionalString(ref writer, column.MissingValueLiteral);
            writer.AppendBoolean(column.RecastDefault);
        }
        return writer.ToArray();
    }

    // Consecutive repeated identities share one tuple while the decoded vector still records every call.
    // A nested expression such as ABS(ABS(...)) must not repeat a full signature at every level on disk.
    private static void AppendFunctionCalls(ref DatabaseKeyWriter writer, IReadOnlyList<string> calls, int expressionLength)
    {
        if (calls.Count > expressionLength)
        {
            throw new SqlCatalogException("CS06059", ErrorMessages.CatalogAppendFunctionCallsSqlCatalog);
        }
        int runs = 0;
        for (int index = 0; index < calls.Count; index++)
        {
            if (index == 0 || !string.Equals(calls[index - 1], calls[index], StringComparison.Ordinal)) { runs++; }
        }
        writer.AppendInt32(runs);
        for (int start = 0; start < calls.Count;)
        {
            int end = start + 1;
            while (end < calls.Count && string.Equals(calls[start], calls[end], StringComparison.Ordinal)) { end++; }
            writer.AppendString(calls[start], Collation.Binary).AppendInt32(end - start);
            start = end;
        }
    }

    private static IReadOnlyList<string> ReadFunctionCalls(ref DatabaseKeyReader reader, int expressionLength)
    {
        int runs = reader.ReadInt32();
        if (runs < 0 || runs > expressionLength)
        {
            throw new SqlCatalogException("CS06059", ErrorMessages.SqlCatalogReadFunctionCallsSqlCatalog);
        }
        var calls = new List<string>();
        for (int index = 0; index < runs; index++)
        {
            string identity = reader.ReadString(out _);
            int count = reader.ReadInt32();
            // Every call requires at least one character in its expression. This parser-independent
            // bound rejects damaged expansion counts before allocating, without summation overflow.
            if (string.IsNullOrWhiteSpace(identity) || count <= 0 || count > expressionLength - calls.Count)
            {
                throw new SqlCatalogException("CS06059", ErrorMessages.SqlCatalogReadFunctionCallsSqlCatalog2);
            }
            for (int repeat = 0; repeat < count; repeat++) { calls.Add(identity); }
        }
        return calls;
    }

    private static SqlCatalogTable DecodeTable(ref DatabaseKeyReader reader)
    {
        ulong objectId = (ulong)reader.ReadInt64();
        string schema = reader.ReadString(out _);
        string name = reader.ReadString(out _);
        int columnCount = reader.ReadInt32();

        var columns = new List<SqlCatalogColumn>(columnCount);
        for (int i = 0; i < columnCount; i++)
        {
            string columnName = reader.ReadString(out _);
            var type = (DatabaseType)reader.ReadInt8();
            int maxLength = reader.ReadInt32();
            int precision = reader.ReadInt32();
            int scale = reader.ReadInt32();
            bool nullable = reader.ReadBoolean();

            string? defaultLiteral = null;
            if (reader.PeekType() == DatabaseType.Null)
            {
                reader.ReadNull();
            }
            else
            {
                defaultLiteral = reader.ReadString(out _);
            }

            columns.Add(new SqlCatalogColumn(
                columnName,
                new DatabaseTypeInfo(
                    type,
                    maxLength < 0 ? null : maxLength,
                    precision < 0 ? null : precision,
                    scale < 0 ? null : scale),
                nullable,
                defaultLiteral));
        }

        int primaryKeyCount = reader.ReadInt32();
        var primaryKey = new List<string>(primaryKeyCount);
        for (int i = 0; i < primaryKeyCount; i++)
        {
            primaryKey.Add(reader.ReadString(out _));
        }

        var (owner, owningSchema) = ReadOwnership(ref reader, allowTrailing: true);
        var constraints = new List<SqlCatalogConstraint>();
        var dropped = new List<int>();
        if (!reader.IsAtEnd)
        {
            // The historical extension labels describe the fields below. Format 10 also
            // requires the call vectors after them; an older record cannot load successfully.
            int version = reader.ReadInt32();
            if (version != tableExtensionVersion)
            {
                throw new SqlCatalogException("CS06060", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.SqlCatalogDecodeTableSqlCatalog, version));
            }
            int count = reader.ReadInt32();
            if (count < 0)
            {
                throw new SqlCatalogException("CS06059", ErrorMessages.SqlCatalogDecodeTableSqlCatalog2);
            }
            for (int index = 0; index < count; index++)
            {
                string constraintName = reader.ReadString(out _);
                var kind = (SqlCatalogConstraintKind)reader.ReadInt8();
                var localColumns = ReadColumnNames(ref reader);
                string? referencedSchema = ReadOptionalString(ref reader);
                string? referencedTable = ReadOptionalString(ref reader);
                var referencedColumns = ReadColumnNames(ref reader);
                string? expression = ReadOptionalString(ref reader);
                var onDelete = (SqlCatalogReferentialAction)reader.ReadInt8();
                var onUpdate = (SqlCatalogReferentialAction)reader.ReadInt8();
                try
                {
                    constraints.Add(new SqlCatalogConstraint(constraintName, kind, localColumns,
                        referencedSchema, referencedTable, referencedColumns, expression, onDelete, onUpdate: onUpdate));
                }
                catch (ArgumentException exception)
                {
                    throw new SqlCatalogException("CS06059", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.SqlCatalogDecodeTableSqlCatalog3, constraintName, exception.Message));
                }
            }
            if (version >= 2)
            {
                for (int index = 0; index < columns.Count; index++)
                {
                    int id = reader.ReadInt32();
                    if (id < -1 || id > byte.MaxValue)
                    {
                        throw new SqlCatalogException("CS06059", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.SqlCatalogDecodeTableSqlCatalog4, id));
                    }
                    var column = columns[index];
                    columns[index] = new SqlCatalogColumn(column.Name, column.Type, column.IsNullable,
                        column.DefaultLiteral, id < 0 ? null : Collation.FromId((byte)id));
                }
            }
            if (version >= 3)
            {
                int droppedCount = reader.ReadInt32();
                if (droppedCount < 0)
                {
                    throw new SqlCatalogException("CS06059", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.SqlCatalogDecodeTableSqlCatalog5, droppedCount, schema, name));
                }
                for (int index = 0; index < droppedCount; index++)
                {
                    dropped.Add(reader.ReadInt32());
                }
            }
            if (version >= 4)
            {
                for (int index = 0; index < columns.Count; index++)
                {
                    string? domain = ReadOptionalString(ref reader);
                    if (domain is null)
                    {
                        continue;
                    }
                    if (string.IsNullOrWhiteSpace(domain))
                    {
                        throw new SqlCatalogException("CS06059", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.SqlCatalogDecodeTableSqlCatalog6, columns[index].Name, schema, name));
                    }
                    var column = columns[index];
                    columns[index] = new SqlCatalogColumn(column.Name, column.Type, column.IsNullable,
                        column.DefaultLiteral, column.Collation, domain);
                }
            }
        }
        for (int index = 0; index < columns.Count; index++)
        {
            if (reader.IsAtEnd)
            {
                throw new SqlCatalogException("CS06059", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.SqlCatalogDecodeTableSqlCatalog7, schema, name));
            }
            var column = columns[index];
            columns[index] = new SqlCatalogColumn(column.Name, column.Type, column.IsNullable,
                column.DefaultLiteral, column.Collation, column.DomainName, ReadFunctionCalls(ref reader, column.DefaultLiteral?.Length ?? 0));
        }
        for (int index = 0; index < constraints.Count; index++)
        {
            if (reader.IsAtEnd)
            {
                throw new SqlCatalogException("CS06059", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.SqlCatalogDecodeTableSqlCatalog7, schema, name));
            }
            var constraint = constraints[index];
            constraints[index] = new SqlCatalogConstraint(constraint.Name, constraint.Kind, constraint.Columns,
                constraint.ReferencedSchema, constraint.ReferencedTable, constraint.ReferencedColumns,
                constraint.CheckExpression, constraint.OnDelete, ReadFunctionCalls(ref reader, constraint.CheckExpression?.Length ?? 0), constraint.OnUpdate);
        }
        for (int index = 0; index < columns.Count; index++)
        {
            if (reader.ReadBoolean())
            {
                bool always = reader.ReadBoolean();
                long start = reader.ReadInt64();
                long increment = reader.ReadInt64();
                var column = columns[index];
                if (increment == 0 || column.IsNullable)
                {
                    throw new SqlCatalogException("CS06059", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.IdentityMetadataInvalid, column.Name));
                }
                var identity = new SqlCatalogIdentity(always, start, increment);
                columns[index] = new SqlCatalogColumn(column.Name, column.Type, column.IsNullable,
                    column.DefaultLiteral, column.Collation, column.DomainName, column.DefaultFunctionCalls, identity);
            }
        }
        for (int index = 0; index < columns.Count; index++)
        {
            var column = columns[index];
            columns[index] = new SqlCatalogColumn(column.Name, column.Type, column.IsNullable,
                column.DefaultLiteral, column.Collation, column.DomainName, column.DefaultFunctionCalls, column.Identity,
                ReadOptionalString(ref reader)) { RecastDefault = reader.ReadBoolean() };
        }
        ValidateColumns(schema, name, columns, primaryKey);
        if (!reader.IsAtEnd)
        {
            throw new SqlCatalogException("CS06059", ErrorMessages.SqlCatalogDecodeTableSqlCatalog8);
        }
        SqlCatalogTable table;
        try
        {
            table = new SqlCatalogTable(objectId, schema, name, columns, primaryKey, owner, owningSchema, constraints, dropped);
        }
        catch (ArgumentException exception)
        {
            throw new SqlCatalogException("CS06059", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.SqlCatalogDecodeTableSqlCatalog9, schema, name, exception.Message));
        }
        ValidateConstraints(table);
        return table;
    }

    private static byte[] EncodeIndex(SqlCatalogIndex index)
        => EnsureStorable(EncodeIndexRecord(index), string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.CatalogIndexSubject, index.Name));

    private static byte[] EncodeIndexRecord(SqlCatalogIndex index)
    {
        var writer = new DatabaseKeyWriter();
        writer.AppendInt32(indexRecordKind)
              .AppendInt64((long)index.TableObjectId)
              .AppendString(index.Name, Collation.Binary)
              .AppendBoolean(index.IsUnique)
              .AppendInt32(index.ColumnNames.Count);

        foreach (string column in index.ColumnNames)
        {
            writer.AppendString(column, Collation.Binary);
        }

        AppendOwnership(ref writer, index.Owner, index.OwningSchema);
        writer.AppendInt32(2).AppendBoolean(index.IsPrimaryKey);
        AppendOptionalString(ref writer, index.Predicate);
        writer.AppendInt32(index.PredicateColumns.Count);
        foreach (string column in index.PredicateColumns) { writer.AppendString(column, Collation.Binary); }
        writer.AppendInt32(index.PredicateFunctionCalls.Count);
        foreach (string call in index.PredicateFunctionCalls) { writer.AppendString(call, Collation.Binary); }
        return writer.ToArray();
    }

    private static SqlCatalogIndex DecodeIndex(ref DatabaseKeyReader reader)
    {
        ulong tableObjectId = (ulong)reader.ReadInt64();
        string name = reader.ReadString(out _);
        bool isUnique = reader.ReadBoolean();
        int columnCount = reader.ReadInt32();

        var columns = new List<string>(columnCount);
        for (int i = 0; i < columnCount; i++)
        {
            columns.Add(reader.ReadString(out _));
        }

        var (owner, owningSchema) = ReadOwnership(ref reader, allowTrailing: true);
        int version = reader.ReadInt32();
        if (version != 2)
        {
            throw new SqlCatalogException("CS06060", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.SqlCatalogDecodeIndexSqlCatalog, version));
        }
        bool isPrimaryKey = reader.ReadBoolean();
        string? predicate = ReadOptionalString(ref reader);
        int dependencies = reader.ReadInt32();
        var predicateColumns = new List<string>();
        for (int i = 0; i < dependencies; i++) { predicateColumns.Add(reader.ReadString(out _)); }
        int calls = reader.ReadInt32();
        var functionCalls = new List<string>();
        for (int i = 0; i < calls; i++) { functionCalls.Add(reader.ReadString(out _)); }
        if (!reader.IsAtEnd || dependencies < 0 || calls < 0 || (isPrimaryKey && (!isUnique || predicate is not null)) ||
            predicate is null && (dependencies != 0 || calls != 0) || predicate is not null && string.IsNullOrWhiteSpace(predicate))
        {
            throw new SqlCatalogException("CS06059", ErrorMessages.SqlCatalogDecodeIndexSqlCatalog2);
        }
        return new SqlCatalogIndex(tableObjectId, name, columns, isUnique, owner, owningSchema, isPrimaryKey,
            predicate, predicateColumns, functionCalls);
    }

    private static void ValidateConstraints(SqlCatalogTable table)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (SqlCatalogConstraint constraint in table.Constraints)
        {
            if (!names.Add(constraint.Name))
            {
                throw new SqlCatalogException("CS03050", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.CatalogValidateConstraintsSqlCatalog, constraint.Name, table.Schema, table.Name));
            }
            foreach (string column in constraint.Columns)
            {
                if (table.FindColumn(column) is null)
                {
                    throw new SqlCatalogException("CS01015", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.CatalogValidateConstraintsSqlCatalog2, constraint.Name, column));
                }
            }
        }
    }

    private static IReadOnlyList<string> ReadColumnNames(ref DatabaseKeyReader reader)
    {
        int count = reader.ReadInt32();
        if (count < 0)
        {
            throw new SqlCatalogException("CS06059", ErrorMessages.SqlCatalogReadColumnNamesSqlCatalog);
        }
        var columns = new List<string>();
        for (int index = 0; index < count; index++)
        {
            columns.Add(reader.ReadString(out _));
        }
        return columns;
    }

    private static void AppendOptionalString(ref DatabaseKeyWriter writer, string? value)
    {
        if (value is null)
        {
            writer.AppendNull();
        }
        else
        {
            writer.AppendString(value, Collation.Binary);
        }
    }

    private static string? ReadOptionalString(ref DatabaseKeyReader reader)
    {
        if (reader.PeekType() == DatabaseType.Null)
        {
            reader.ReadNull();
            return null;
        }
        return reader.ReadString(out _);
    }

    /// <summary>
    /// Reserves a durable object identity for a table before its enforcing indexes are built.
    /// </summary>
    /// <param name="schema">The SQL namespace.</param>
    /// <param name="name">The table name.</param>
    /// <param name="columns">The ordered column definitions.</param>
    /// <param name="primaryKeyColumns">The primary-key columns, or null for no primary key.</param>
    /// <param name="constraints">The foreign-key and check definitions.</param>
    /// <param name="owner">The table's ownership classification.</param>
    /// <param name="owningSchema">The compiled schema name for schema-owned tables; otherwise null.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The unpublished table definition with an assigned object identity.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException">A name is empty or whitespace, a constraint is null, or ownership metadata is inconsistent.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The ownership classification is unsupported.</exception>
    /// <exception cref="SqlCatalogException">The table exists or its definition is invalid.</exception>
    /// <exception cref="StorageException">The backing storage cannot persist the metadata change.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    /// <remarks>
    /// Only the identity counter is persisted. The table remains invisible until
    /// <see cref="PublishTableAsync"/> succeeds; abandoning a reservation never reuses
    /// its identity. Callers serialize DDL; a reservation does not lock the name.
    /// </remarks>
    /// <exception cref="StorageOfflineException">The catalog storage is offline; reopen the database before reading or changing its metadata.</exception>
    public ValueTask<SqlCatalogTable> ReserveTableAsync(
        string schema,
        string name,
        IReadOnlyList<SqlCatalogColumn> columns,
        IReadOnlyList<string>? primaryKeyColumns,
        IReadOnlyList<SqlCatalogConstraint> constraints,
        DatabaseObjectOwner owner,
        string? owningSchema,
        CancellationToken cancellationToken = default)
        => ReserveTableCoreAsync(schema, name, columns, primaryKeyColumns, constraints, owner, owningSchema, cancellationToken, publication: null);

    /// <summary>
    /// Reserves a durable object identity for a table before its enforcing indexes are built.
    /// </summary>
    /// <param name="schema">The SQL namespace.</param>
    /// <param name="name">The table name.</param>
    /// <param name="columns">The ordered column definitions.</param>
    /// <param name="primaryKeyColumns">The primary-key columns, or null for no primary key.</param>
    /// <param name="constraints">The foreign-key and check definitions.</param>
    /// <param name="owner">The table's ownership classification.</param>
    /// <param name="owningSchema">The compiled schema name for schema-owned tables; otherwise null.</param>
    /// <param name="publication">The expected statement target and exact preceding receipt and catalog generation, validated before commit. Claims are derived from this mutation.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The unpublished table definition with an assigned object identity.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException">A name is empty or whitespace, a constraint is null, or ownership metadata is inconsistent.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The ownership classification is unsupported.</exception>
    /// <exception cref="SqlCatalogException">The table exists or its definition is invalid.</exception>
    /// <exception cref="StorageException">The backing storage cannot persist the metadata change.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    /// <remarks>
    /// The identity counter and validated receipt are persisted together. The receipt retains
    /// its preceding completed prefix and object claims, and advances its generation.
    /// Final publication requires a new expectation with that exact reserved receipt and
    /// generation. The table remains invisible until
    /// <see cref="PublishTableAsync"/> succeeds; abandoning a reservation never reuses
    /// its identity. Callers serialize DDL; a reservation does not lock the name.
    /// </remarks>
    /// <exception cref="StorageOfflineException">The catalog storage is offline; reopen the database before reading or changing its metadata.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="publication"/> is null.</exception>
    /// <exception cref="SqlCatalogException">The target or preceding state does not match, the script identity changed, or the receipt exceeds the record limit.</exception>
    public ValueTask<SqlCatalogTable> ReserveTableAsync(
        string schema,
        string name,
        IReadOnlyList<SqlCatalogColumn> columns,
        IReadOnlyList<string>? primaryKeyColumns,
        IReadOnlyList<SqlCatalogConstraint> constraints,
        DatabaseObjectOwner owner,
        string? owningSchema,
        SqlCatalogScriptPublication publication,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(publication);
        return ReserveTableCoreAsync(schema, name, columns, primaryKeyColumns, constraints, owner, owningSchema, cancellationToken, publication);
    }

    private ValueTask<SqlCatalogTable> ReserveTableCoreAsync(
        string schema,
        string name,
        IReadOnlyList<SqlCatalogColumn> columns,
        IReadOnlyList<string>? primaryKeyColumns,
        IReadOnlyList<SqlCatalogConstraint> constraints,
        DatabaseObjectOwner owner,
        string? owningSchema,
        CancellationToken cancellationToken,
        SqlCatalogScriptPublication? publication,
        bool prepareOwner = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(constraints);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            ThrowIfOffline();
            if (RelationExists(schema, name))
            {
                if (_sequences.ContainsKey((schema, name))) { throw RelationNameCollision(schema, name); }
                throw new SqlCatalogException("CS03049", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.RenameTableCollision, schema, name));
            }
            ValidateColumns(schema, name, columns, primaryKeyColumns);
            var table = new SqlCatalogTable(_nextObjectId, schema, name, columns, primaryKeyColumns, owner, owningSchema, constraints);
            ValidateConstraints(table);
            // Persist the identity before physical trees are created. A crash can
            // leave unused trees, but can never reuse their identity for a table.
            // Refuse an unstorable definition before reserving an owner or evaluating CTAS.
            _ = EncodeTable(table);
            using var transaction = _storage.BeginTransaction();
            var intent = prepareOwner ? _storage.InsertRow(transaction, table.ObjectId,
                SqlReclamationRecord.Encode(table.ObjectId, schema, name)) : ((PageId PageId, int SlotIndex)?)null;
            CommitCatalogChange(transaction, checked(_nextObjectId + 1), publication, reservationOnly: true, actualObject: SqlObjectIdentity.Table(schema, name));
            if (intent is { } location)
            {
                _pendingReclamations.Add(table.ObjectId, new SqlReclamationIntent(location.PageId, location.SlotIndex));
                _preparedTruncateOwners.Add(table.ObjectId);
            }
            return new ValueTask<SqlCatalogTable>(table);
        }
    }

    /// <summary>
    /// Publishes a table definition and its new index descriptions with the complete physical
    /// registration set atomically.
    /// </summary>
    /// <param name="table">The reserved table, or a replacement definition with the existing table's identity.</param>
    /// <param name="indexes">New index descriptions whose physical trees have already been committed.</param>
    /// <param name="registrations">A physical registration snapshot containing exactly one registration per new index, matching its table, name (case-insensitively), and uniqueness, with B-tree kind and a positive root page id. Only those registrations are merged into the current directory.</param>
    /// <param name="replaceExisting">Whether the definition replaces an existing table with the same identity.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>A task representing durable publication of all supplied metadata.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="SqlCatalogException">
    /// The identity was not reserved; the identity, table definition, or index registrations are
    /// inconsistent; or the definition does not keep the table's physical column layout.
    /// </exception>
    /// <exception cref="DatabaseTypeException">Metadata text contains invalid UTF-16.</exception>
    /// <exception cref="StorageException">The backing storage cannot persist the metadata change.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    /// <remarks>
    /// New definitions require a nonzero identity already allocated by this catalog.
    /// Callers serialize DDL and commit enforcing index trees before publication.
    /// Existing index descriptions are retained during replacement. A new definition has no
    /// dropped columns, and a replacement keeps the physical column layout of the definition it
    /// replaces (<see cref="SqlCatalogTable.DroppedColumnOrdinals"/> unchanged, every existing
    /// live column in place); it may only append columns, so no stored row is decoded onto
    /// another column. The catalog owns metadata persistence; physical tree creation and row
    /// validation remain the caller's responsibility.
    /// </remarks>
    /// <exception cref="StorageOfflineException">The catalog storage is offline; reopen the database before reading or changing its metadata.</exception>
    public ValueTask PublishTableAsync(
        SqlCatalogTable table,
        IReadOnlyList<SqlCatalogIndex> indexes,
        IReadOnlyList<BTreeIndexRegistration> registrations,
        bool replaceExisting = false,
        CancellationToken cancellationToken = default)
        => PublishTableCoreAsync(table, indexes, registrations, replaceExisting, cancellationToken, publication: null);

    /// <summary>
    /// Publishes a table definition and its new index descriptions with the complete physical
    /// registration set atomically.
    /// </summary>
    /// <param name="table">The reserved table, or a replacement definition with the existing table's identity.</param>
    /// <param name="indexes">New index descriptions whose physical trees have already been committed.</param>
    /// <param name="registrations">A physical registration snapshot containing exactly one registration per new index, matching its table, name (case-insensitively), and uniqueness, with B-tree kind and a positive root page id. Only those registrations are merged into the current directory.</param>
    /// <param name="replaceExisting">Whether the definition replaces an existing table with the same identity.</param>
    /// <param name="publication">The expected statement target and exact preceding receipt and catalog generation, validated before commit. Claims are derived from this mutation.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>A task representing durable publication of all supplied metadata.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="SqlCatalogException">
    /// The identity was not reserved; the identity, table definition, or index registrations are
    /// inconsistent; or the definition does not keep the table's physical column layout.
    /// </exception>
    /// <exception cref="DatabaseTypeException">Metadata text contains invalid UTF-16.</exception>
    /// <exception cref="StorageException">The backing storage cannot persist the metadata change.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    /// <remarks>
    /// New definitions require a nonzero identity already allocated by this catalog.
    /// Callers serialize DDL and commit enforcing index trees before publication.
    /// Existing index descriptions are retained during replacement. A new definition has no
    /// dropped columns, and a replacement keeps the physical column layout of the definition it
    /// replaces (<see cref="SqlCatalogTable.DroppedColumnOrdinals"/> unchanged, every existing
    /// live column in place); it may only append columns, so no stored row is decoded onto
    /// another column. The catalog owns metadata persistence; physical tree creation and row
    /// validation remain the caller's responsibility.
    /// </remarks>
    /// <exception cref="StorageOfflineException">The catalog storage is offline; reopen the database before reading or changing its metadata.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="publication"/> is null.</exception>
    /// <exception cref="SqlCatalogException">The target or preceding state does not match, the script identity changed, or the receipt exceeds the record limit.</exception>
    public ValueTask PublishTableAsync(
        SqlCatalogTable table,
        IReadOnlyList<SqlCatalogIndex> indexes,
        IReadOnlyList<BTreeIndexRegistration> registrations,
        bool replaceExisting,
        SqlCatalogScriptPublication publication,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(publication);
        return PublishTableCoreAsync(table, indexes, registrations, replaceExisting, cancellationToken, publication);
    }

    private ValueTask PublishTableCoreAsync(
        SqlCatalogTable table,
        IReadOnlyList<SqlCatalogIndex> indexes,
        IReadOnlyList<BTreeIndexRegistration> registrations,
        bool replaceExisting,
        CancellationToken cancellationToken,
        SqlCatalogScriptPublication? publication)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(indexes);
        ArgumentNullException.ThrowIfNull(registrations);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            ThrowIfOffline();
            if (!replaceExisting && (table.ObjectId == 0 || table.ObjectId >= _nextObjectId))
            {
                throw new SqlCatalogException("CS05060", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.PublishTableCoreAsyncSqlCatalog, table.Schema, table.Name));
            }
            _tables.TryGetValue((table.Schema, table.Name), out TableSlot? existing);
            if (!replaceExisting && existing is not null)
            {
                throw new SqlCatalogException("CS03049", string.Format(System.Globalization.CultureInfo.CurrentCulture,
                    ErrorMessages.RenameTableCollision, table.Schema, table.Name));
            }
            if (replaceExisting ? existing is null || existing.Table.ObjectId != table.ObjectId :
                existing is not null || FindTableByObjectId(table.ObjectId) is not null)
            {
                throw new SqlCatalogException("CS05060", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.PublishTableCoreAsyncSqlCatalog2, table.Schema, table.Name));
            }
            if (SequenceNameExists(table.Schema, table.Name)) { throw RelationNameCollision(table.Schema, table.Name); }
            if (_views.ContainsKey(SqlObjectIdentity.View(table.Schema, table.Name))) { throw new SqlCatalogException("CS03033", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.ViewExists, table.Schema, table.Name)); }
            if (existing is not null)
            {
                foreach (var column in existing.Table.Columns)
                {
                    var replacement = table.FindColumn(column.Name);
                    if (replacement is null) { EnsureNoViewReads(existing.Table, column.Name); }
                    else if (replacement.Type.Type != column.Type.Type || replacement.Type.MaxLength != column.Type.MaxLength ||
                        replacement.Type.Precision != column.Type.Precision || replacement.Type.Scale != column.Type.Scale) { EnsureNoViewReads(existing.Table, column.Name, rename: true); }
                }
            }
            EnsurePhysicalLayoutKept(existing?.Table, table);
            ValidateColumns(table.Schema, table.Name, table.Columns, table.PrimaryKeyColumns);
            ValidateConstraints(table);
            var names = new HashSet<string>(table.Constraints.Select(constraint => constraint.Name), StringComparer.OrdinalIgnoreCase);
            foreach (SqlCatalogIndex index in GetIndexes(table.ObjectId))
            {
                if (!names.Add(index.Name) || index.ColumnNames.Concat(index.PredicateColumns).Any(column => table.FindColumn(column) is null))
                {
                    throw new SqlCatalogException("CS05060", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.PublishTableCoreAsyncSqlCatalog3, index.Name));
                }
            }
            foreach (SqlCatalogIndex index in indexes)
            {
                if (index.TableObjectId != table.ObjectId || !names.Add(index.Name) ||
                    index.ColumnNames.Concat(index.PredicateColumns).Any(column => table.FindColumn(column) is null))
                {
                    throw new SqlCatalogException("CS05060", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.PublishTableCoreAsyncSqlCatalog4, index.Name, table.Schema, table.Name));
                }
            }
            var mergedRegistrations = MergePublishedRegistrations(registrations, indexes);
            using var transaction = _storage.BeginTransaction();
            var tableLocation = UpsertRecord(transaction, existing?.Location, EncodeTable(table));
            var slots = indexes.Select(index => new IndexSlot(index, _storage.InsertRow(transaction, EncodeIndex(index)))).ToArray();
            var registrationsLocation = UpsertRecord(transaction, _registrationsLocation, EncodeRegistrations(mergedRegistrations));
            // CTAS's unreachable owner stays reclaimable until this same catalog commit
            // publishes its complete definition. Format 26 already understands kind 14.
            bool prepared = !replaceExisting && _preparedTruncateOwners.Contains(table.ObjectId);
            if (prepared)
            {
                var intent = _pendingReclamations[table.ObjectId];
                _storage.DeleteRow(transaction, intent.PageId, intent.SlotIndex);
            }
            ulong next = _nextObjectId;
            var owned = StageOwnedSequences(transaction, existing?.Table, table, ref next);
            CommitCatalogChange(transaction, next, publication: publication, actualObject: SqlObjectIdentity.Table(table.Schema, table.Name), createsObject: !replaceExisting);
            owned();
            if (prepared)
            {
                _pendingReclamations.Remove(table.ObjectId);
                _preparedTruncateOwners.Remove(table.ObjectId);
            }
            _tables[(table.Schema, table.Name)] = new TableSlot(table, tableLocation);
            foreach (IndexSlot slot in slots)
            {
                _indexes[(table.ObjectId, slot.Index.Name)] = slot;
            }
            _registrationsLocation = registrationsLocation;
            _registrations = mergedRegistrations;
            return default;
        }
    }

    /// <summary>
    /// Refuses a published definition that would move a stored component to another
    /// column. A new table starts with no dropped column. A replacement keeps the
    /// definition it replaces as its prefix: the same dropped ordinals and the same live
    /// columns, by name, at the same positions, so every column keeps its physical
    /// ordinal; it may only append columns. Every stored version is decoded through these
    /// ordinals, so a replacement that renumbered them would read each value from
    /// another column's component.
    /// </summary>
    /// <param name="existing">The definition being replaced, or null for a new table.</param>
    /// <param name="table">The definition being published.</param>
    /// <exception cref="SqlCatalogException">The published definition renumbers a physical column.</exception>
    private static void EnsurePhysicalLayoutKept(SqlCatalogTable? existing, SqlCatalogTable table)
    {
        if (existing is null)
        {
            if (table.DroppedColumnOrdinals.Count > 0)
            {
                throw new SqlCatalogException("CS05060", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.EnsurePhysicalLayoutKeptSqlCatalog, table.Schema, table.Name));
            }

            return;
        }

        bool kept = existing.DroppedColumnOrdinals.SequenceEqual(table.DroppedColumnOrdinals) &&
            table.Columns.Count >= existing.Columns.Count;
        for (int ordinal = 0; kept && ordinal < existing.Columns.Count; ordinal++)
        {
            kept = string.Equals(existing.Columns[ordinal].Name, table.Columns[ordinal].Name, StringComparison.OrdinalIgnoreCase);
            if (!string.Equals(existing.Columns[ordinal].MissingValueLiteral, table.Columns[ordinal].MissingValueLiteral, StringComparison.Ordinal))
            {
                throw new SqlCatalogException("CS05060", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.EnsurePhysicalLayoutKeptSqlCatalog2, existing.Columns[ordinal].Name));
            }
        }

        if (!kept)
        {
            throw new SqlCatalogException("CS05060",
                string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.EnsurePhysicalLayoutKeptSqlCatalog3, table.Schema, table.Name));
        }
    }

    /// <summary>
    /// Adds a persisted foreign-key or check constraint to a table, for the catalog's tests; the
    /// engine publishes a table's constraints through <see cref="ReserveTableAsync"/> and
    /// <see cref="PublishTableAsync"/>.
    /// </summary>
    internal ValueTask<SqlCatalogTable> AddConstraintAsync(
        string schema, string name, SqlCatalogConstraint constraint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(constraint);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            ThrowIfOffline();
            var slot = GetSlot(schema, name);
            if (_indexes.ContainsKey((slot.Table.ObjectId, constraint.Name)))
            {
                throw new SqlCatalogException("CS03050", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.CatalogAddConstraintAsyncSqlCatalog, constraint.Name, schema, name));
            }
            var updated = new SqlCatalogTable(slot.Table.ObjectId, schema, name, slot.Table.Columns,
                slot.Table.PrimaryKeyColumns, slot.Table.Owner, slot.Table.OwningSchema,
                slot.Table.Constraints.Append(constraint).ToArray(), slot.Table.DroppedColumnOrdinals);
            ValidateConstraints(updated);
            ReplaceTable(slot, updated, publication: null);
            return new ValueTask<SqlCatalogTable>(updated);
        }
    }

    /// <summary>
    /// Drops a persisted foreign-key or check constraint from a table.
    /// </summary>
    /// <param name="schema">The SQL namespace.</param>
    /// <param name="name">The table name.</param>
    /// <param name="constraintName">The constraint name, compared case-insensitively.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The durably updated table description.</returns>
    /// <exception cref="ArgumentNullException">A required name is null.</exception>
    /// <exception cref="ArgumentException">A required name is empty or whitespace.</exception>
    /// <exception cref="SqlCatalogException">The table or constraint does not exist.</exception>
    /// <exception cref="StorageException">The backing storage cannot persist the metadata change.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    /// <exception cref="StorageOfflineException">The catalog storage is offline; reopen the database before reading or changing its metadata.</exception>
    public ValueTask<SqlCatalogTable> DropConstraintAsync(
        string schema,
        string name,
        string constraintName,
        CancellationToken cancellationToken = default)
        => DropConstraintCoreAsync(schema, name, constraintName, cancellationToken, publication: null);

    /// <summary>
    /// Drops a persisted foreign-key or check constraint from a table.
    /// </summary>
    /// <param name="schema">The SQL namespace.</param>
    /// <param name="name">The table name.</param>
    /// <param name="constraintName">The constraint name, compared case-insensitively.</param>
    /// <param name="publication">The expected statement target and exact preceding receipt and catalog generation, validated before commit. Claims are derived from this mutation.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The durably updated table description.</returns>
    /// <exception cref="ArgumentNullException">A required name is null.</exception>
    /// <exception cref="ArgumentException">A required name is empty or whitespace.</exception>
    /// <exception cref="SqlCatalogException">The table or constraint does not exist.</exception>
    /// <exception cref="StorageException">The backing storage cannot persist the metadata change.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    /// <exception cref="StorageOfflineException">The catalog storage is offline; reopen the database before reading or changing its metadata.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="publication"/> is null.</exception>
    /// <exception cref="SqlCatalogException">The target or preceding state does not match, the script identity changed, or the receipt exceeds the record limit.</exception>
    public ValueTask<SqlCatalogTable> DropConstraintAsync(
        string schema,
        string name,
        string constraintName,
        SqlCatalogScriptPublication publication,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(publication);
        return DropConstraintCoreAsync(schema, name, constraintName, cancellationToken, publication);
    }

    private ValueTask<SqlCatalogTable> DropConstraintCoreAsync(
        string schema,
        string name,
        string constraintName,
        CancellationToken cancellationToken,
        SqlCatalogScriptPublication? publication)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(constraintName);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            ThrowIfOffline();
            var slot = GetSlot(schema, name);
            var constraints = slot.Table.Constraints.Where(constraint =>
                !string.Equals(constraint.Name, constraintName, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (constraints.Length == slot.Table.Constraints.Count)
            {
                throw new SqlCatalogException("CS01052", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.DropConstraintCoreAsyncSqlCatalog, constraintName, schema, name));
            }
            var updated = new SqlCatalogTable(slot.Table.ObjectId, schema, name, slot.Table.Columns,
                slot.Table.PrimaryKeyColumns, slot.Table.Owner, slot.Table.OwningSchema, constraints, slot.Table.DroppedColumnOrdinals);
            ReplaceTable(slot, updated, publication);
            return new ValueTask<SqlCatalogTable>(updated);
        }
    }

    private static void AppendOwnership(ref DatabaseKeyWriter writer, DatabaseObjectOwner owner, string? owningSchema)
    {
        writer.AppendInt8((sbyte)owner);
        if (owningSchema is null)
        {
            writer.AppendNull();
        }
        else
        {
            writer.AppendString(owningSchema, Collation.Binary);
        }
    }

    private static (DatabaseObjectOwner Owner, string? OwningSchema) ReadOwnership(ref DatabaseKeyReader reader, bool allowTrailing = false)
    {
        // Existing records end after the original payload. They predate ownership
        // and retain ad-hoc mutability instead of acquiring a guessed schema owner.
        if (reader.IsAtEnd)
        {
            return (DatabaseObjectOwner.Adhoc, null);
        }

        var owner = (DatabaseObjectOwner)reader.ReadInt8();
        string? owningSchema = null;
        if (reader.PeekType() == DatabaseType.Null)
        {
            reader.ReadNull();
        }
        else
        {
            owningSchema = reader.ReadString(out _);
        }

        if ((!allowTrailing && !reader.IsAtEnd) ||
            owner is not DatabaseObjectOwner.Adhoc and not DatabaseObjectOwner.Schema ||
            (owner == DatabaseObjectOwner.Schema ? string.IsNullOrWhiteSpace(owningSchema) : owningSchema is not null))
        {
            throw new SqlCatalogException("CS06059", ErrorMessages.SqlCatalogReadOwnershipSqlCatalog);
        }

        return (owner, owningSchema);
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

        return EnsureStorable(writer.ToArray(), string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.CatalogRegistrationsSubject, registrations.Count));
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

    private static byte[] EncodeSchemaStateChunk(
        string contentHash,
        ulong generation,
        int index,
        int count,
        ReadOnlySpan<byte> document,
        int kind)
    {
        var writer = new DatabaseKeyWriter();
        writer.AppendInt32(kind)
              .AppendString(contentHash, Collation.Binary)
              .AppendInt32(index)
              .AppendInt32(count)
              .AppendBinary(document)
              .AppendInt64((long)generation);
        return writer.ToArray();
    }

    private static SchemaStateChunk DecodeSchemaStateChunk(ref DatabaseKeyReader reader)
    {
        string contentHash = reader.ReadString(out _);
        int index = reader.ReadInt32();
        int count = reader.ReadInt32();
        byte[] document = reader.ReadBinary();
        if (reader.IsAtEnd)
        {
            throw new SqlCatalogException("CS06060",
                ErrorMessages.DecodeSchemaStateChunkSqlCatalog);
        }
        ulong generation = (ulong)reader.ReadInt64();
        if (!reader.IsAtEnd)
        {
            throw new SqlCatalogException("CS06059", ErrorMessages.DecodeSchemaStateChunkSqlCatalog2);
        }
        return new SchemaStateChunk(contentHash, generation, index, count, document, default);
    }

    private static SqlCatalogSchemaState AssembleSchemaState(IReadOnlyList<SchemaStateChunk> chunks)
    {
        SchemaStateChunk first = chunks[0];
        if (first.Count <= 0 || first.Count != chunks.Count)
        {
            throw new SqlCatalogException("CS06059", ErrorMessages.CatalogAssembleSchemaStateSqlCatalog);
        }

        var ordered = new byte[first.Count][];
        int documentLength = 0;

        foreach (SchemaStateChunk chunk in chunks)
        {
            if (chunk.Count != first.Count || chunk.Generation != first.Generation ||
                !string.Equals(chunk.ContentHash, first.ContentHash, StringComparison.Ordinal) ||
                chunk.Index < 0 ||
                chunk.Index >= ordered.Length ||
                ordered[chunk.Index] is not null)
            {
                throw new SqlCatalogException("CS06059", ErrorMessages.CatalogAssembleSchemaStateSqlCatalog2);
            }

            ordered[chunk.Index] = chunk.Document;
            documentLength = checked(documentLength + chunk.Document.Length);
        }

        var document = new byte[documentLength];
        int offset = 0;
        foreach (byte[] chunk in ordered)
        {
            if (chunk is null)
            {
                throw new SqlCatalogException("CS06059", ErrorMessages.CatalogAssembleSchemaStateSqlCatalog3);
            }

            chunk.CopyTo(document, offset);
            offset += chunk.Length;
        }

        try
        {
            return new SqlCatalogSchemaState(first.ContentHash, _strictUtf8.GetString(document), first.Generation);
        }
        catch (DecoderFallbackException)
        {
            throw new SqlCatalogException("CS06059", ErrorMessages.CatalogAssembleSchemaStateSqlCatalog4);
        }
    }

    private sealed record TableSlot(SqlCatalogTable Table, (PageId PageId, int SlotIndex) Location);

    private sealed record IndexSlot(SqlCatalogIndex Index, (PageId PageId, int SlotIndex) Location);

    private sealed record SchemaStateChunk(
        string ContentHash,
        ulong Generation,
        int Index,
        int Count,
        byte[] Document,
        (PageId PageId, int SlotIndex) Location);

    private sealed class IndexNameComparer : IEqualityComparer<(ulong TableObjectId, string Name)>
    {
        internal static IndexNameComparer Instance { get; } = new();

        public bool Equals((ulong TableObjectId, string Name) x, (ulong TableObjectId, string Name) y)
            => x.TableObjectId == y.TableObjectId
            && string.Equals(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((ulong TableObjectId, string Name) value)
            => HashCode.Combine(value.TableObjectId, StringComparer.OrdinalIgnoreCase.GetHashCode(value.Name));
    }

    private sealed class TableNameComparer : IEqualityComparer<(string Schema, string Name)>
    {
        internal static TableNameComparer Instance { get; } = new();

        public bool Equals((string Schema, string Name) x, (string Schema, string Name) y)
            => string.Equals(x.Schema, y.Schema, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Schema, string Name) value)
            => HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.Schema),
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.Name));
    }

    private sealed class DebugView
    {
        public DebugView(global::Assimalign.Cohesion.Database.Sql.Catalog.SqlCatalog value)
        {
            _ = value;
        }
    }
}
