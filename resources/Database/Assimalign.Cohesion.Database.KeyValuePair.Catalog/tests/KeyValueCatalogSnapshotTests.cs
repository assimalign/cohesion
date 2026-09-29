using System.IO;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Indexing;
using Assimalign.Cohesion.Database.KeyValuePair.Storage;

namespace Assimalign.Cohesion.Database.KeyValuePair.Catalog.Tests;

/// <summary>Catalog captures detach their metadata from later publications.</summary>
public sealed class KeyValueCatalogSnapshotTests
{
    /// <summary>Captures retain both their format marker and registrations after catalog changes.</summary>
    /// <returns>The asynchronous test operation.</returns>
    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair.Catalog] - Snapshot: Existing captures survive catalog publication")]
    public async Task CaptureSnapshot_ShouldPreserveCapturedMetadata()
    {
        using var storage = KeyValueStorage.Create(new MemoryStream(), new MemoryStream(), new MemoryStream(), "kv.catalog");
        var catalog = KeyValueCatalog.Open(storage);
        await catalog.SaveIndexRegistrationsAsync([new(1, new IndexDefinition("key", IndexKind.BTree, IsUnique: true), 7)]);
        var before = KeyValueCatalog.CaptureSnapshot(catalog);

        await catalog.SetEntrySpaceFormatVersionAsync(2);
        await catalog.SaveIndexRegistrationsAsync([new(1, new IndexDefinition("key", IndexKind.BTree, IsUnique: true), 11)]);
        var after = KeyValueCatalog.CaptureSnapshot(catalog);

        before.EntrySpaceFormatVersion.ShouldBe(1);
        before.IndexRegistrations.ShouldHaveSingleItem().RootPageId.ShouldBe(7);
        after.EntrySpaceFormatVersion.ShouldBe(2);
        after.IndexRegistrations.ShouldHaveSingleItem().RootPageId.ShouldBe(11);
    }
}
