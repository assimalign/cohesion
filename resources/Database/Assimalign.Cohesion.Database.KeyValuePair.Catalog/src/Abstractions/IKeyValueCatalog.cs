using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Indexing;

namespace Assimalign.Cohesion.Database.KeyValuePair.Catalog;

/// <summary>
/// The metadata catalog of one key-value database: the entry-space format version
/// and the physical registrations of the database's primary key index — persisted
/// through the storage kernel so metadata gets the same durability as data.
/// </summary>
/// <remarks>
/// The key-value model deliberately has a <b>minimal</b> catalog: no schemas, no
/// tables, no constraints beyond key uniqueness (which the primary index itself
/// enforces). What must persist is exactly what re-attaches the database on open —
/// the index registrations (root page ids drift on splits; the engine re-exports at
/// its persistence points) and the entry-space format version (records are not
/// self-describing across format changes). Catalog writes are self-committing:
/// each runs in its own storage transaction on the dedicated catalog file set and
/// is durable when the call returns. Named key spaces (multiple ordered key spaces
/// per database) and per-entry expiration metadata are deferred model features —
/// when they land, their registrations join this catalog.
/// </remarks>
public interface IKeyValueCatalog
{
    /// <summary>
    /// Gets the entry-space format version of the database's data storage: 1 =
    /// MVCC-stamped key/value entry records in the key space's page chain (the
    /// format the model was born on; also the value reported when no marker is
    /// persisted). The engine reads this at open and rejects versions newer than
    /// it understands.
    /// </summary>
    int EntrySpaceFormatVersion { get; }

    /// <summary>
    /// Persists the entry-space format version. Self-committing, like every
    /// catalog write; called by the engine at database creation (the space is
    /// born on the current format).
    /// </summary>
    /// <param name="version">The format version to persist.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    ValueTask SetEntrySpaceFormatVersionAsync(int version, CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists the physical index registrations exported by the index manager, so
    /// the primary key index re-attaches when the database reopens.
    /// </summary>
    /// <param name="registrations">The registrations to persist (replaces the stored set).</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    ValueTask SaveIndexRegistrationsAsync(IReadOnlyList<BTreeIndexRegistration> registrations, CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads the persisted index registrations.
    /// </summary>
    /// <returns>The stored registrations, empty when none were saved.</returns>
    IReadOnlyList<BTreeIndexRegistration> GetIndexRegistrations();
}
