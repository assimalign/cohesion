using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Database.Documents.Internal;
using Assimalign.Cohesion.Database.Documents.Storage;
using Assimalign.Cohesion.Database.Storage;

namespace Assimalign.Cohesion.Database.Documents.Tests;

/// <summary>Hands a prepared file set to the real engine open path so tests can observe its coordinator.</summary>
internal sealed class OpenedDocumentStorageStrategy : DocumentStorageStrategy
{
    private readonly DocumentStorage _storage;

    public OpenedDocumentStorageStrategy(DocumentStorage storage)
    {
        _storage = storage;
    }

    public override DocumentStorage OpenStorage(DatabaseName databaseName, StorageCommitDurability? durability) => _storage;
    public override bool StorageExists(DatabaseName databaseName) => databaseName.ToString() == _storage.Name.ToString();
    public override IEnumerable<DatabaseName> GetDatabaseNames() => [(DatabaseName)_storage.Name.ToString()];
    public override DocumentStorage CreateStorage(DatabaseName databaseName, StorageCommitDurability? durability) => throw new NotSupportedException();
    public override void DropStorage(DatabaseName databaseName) => throw new NotSupportedException();
}
