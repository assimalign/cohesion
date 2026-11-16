using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Indexing;
using Assimalign.Cohesion.Database.Sql.Storage;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Sql.Catalog.Tests;

// Catalog-only fixtures still prepare real owner-tagged trees and a live directory.
// Their memory file combines metadata and physical pages; no engine worker runs here.
internal static class PreparedCatalogIndexes
{
    private static readonly ConditionalWeakTable<SqlCatalog, DirectoryState> _directories = new();

    internal static async ValueTask<BTreeIndexRegistration> CreateAsync(SqlCatalog catalog, SqlStorage storage,
        ulong tableId, IndexDefinition definition)
    {
        var directory = _directories.GetValue(catalog, _ => new DirectoryState(storage));
        catalog.IndexPageOwner = directory.Owner;
        catalog.IndexOwnerKnown = directory.Known;
        ulong owner = catalog.ReserveIndexTreeOwner(tableId, CancellationToken.None);
        await using var transactions = TransactionManager.Create(new LockManager(), VersionStore.CreateInMemory());
        var context = await transactions.BeginAsync();
        using var bracket = storage.BeginTransaction();
        directory.Bracket = bracket;
        try
        {
            await directory.Indexes.CreateIndexAsync(context, owner, definition);
            var registration = directory.Indexes.ExportRegistrations().Single(item => item.ObjectId == owner) with { ObjectId = tableId };
            bracket.Commit();
            await transactions.CommitAsync(context);
            directory.Roots.Add(registration.RootPageId, (registration, owner));
            return registration;
        }
        finally { directory.Bracket = null; }
    }

    private sealed class DirectoryState
    {
        private readonly SqlStorage _storage;
        internal readonly Dictionary<long, (BTreeIndexRegistration Registration, ulong Owner)> Roots = new();
        internal readonly BTreeIndexManager Indexes;
        internal StorageTransaction? Bracket;

        internal DirectoryState(SqlStorage storage)
        {
            _storage = storage;
            Indexes = BTreeIndexManager.Create(new()
            {
                Storage = storage,
                TransactionSource = _ => Bracket ?? throw new InvalidOperationException("No catalog test index bracket."),
            });
        }

        internal ulong Owner(BTreeIndexRegistration registration)
            => Roots.TryGetValue(registration.RootPageId, out var root) ? root.Owner : registration.ObjectId;

        internal bool Known(BTreeIndexRegistration registration)
            => Roots.TryGetValue(registration.RootPageId, out var root) &&
                root.Registration.ObjectId == registration.ObjectId &&
                root.Registration.Definition.Kind == registration.Definition.Kind &&
                root.Registration.Definition.IsUnique == registration.Definition.IsUnique &&
                string.Equals(root.Registration.Definition.Name, registration.Definition.Name, StringComparison.OrdinalIgnoreCase) &&
                Indexes.TryGetIndex(root.Owner, root.Registration.Definition.Name, out _) &&
                _storage.GetOwnerIndexPageCount(root.Owner) != 0;
    }
}
