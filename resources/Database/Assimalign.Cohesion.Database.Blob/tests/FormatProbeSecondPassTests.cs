using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Blob.Catalog;
using Assimalign.Cohesion.Database.Blob.Storage;
using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Blob.Tests;

[Collection(nameof(FormatProbeFixTests))]
public sealed class FormatProbeSecondPassTests
{
    [Fact(DisplayName = "Cohesion Test [Blob] - Open: a recovered probe performs the pre-scrub catalog check once (#1744)")]
    public async Task Open_UncleanJournal_ShouldCheckCatalogOnceBeforeScrub()
    {
        // Arrange.
        string root = Path.Combine(Path.GetTempPath(), "cohesion-blob-checks-" + Guid.NewGuid().ToString("N"));
        try
        {
            await FormatProbeCrash.SeedAsync("Blob", root, legacy: false);
            await using var engine = BlobDatabaseEngine.Create("probe", new() { RootPath = root });
            int checks = 0;
            engine.FormatCheckStarting = () => checks++;

            // Act.
            await engine.OpenDatabaseAsync("probe", CancellationToken.None);

            // Assert: BlobCatalog.Open still builds the catalog after scrub.
            checks.ShouldBe(1);
        }
        finally { if (Directory.Exists(root)) { Directory.Delete(root, recursive: true); } }
    }

    [Fact(DisplayName = "Cohesion Test [Blob] - Open: a failed writable-path format gate disposes its coordinator (#1744)")]
    public async Task Construct_FormatGateFailure_ShouldDisposeCoordinator()
    {
        // Arrange: no probe ran, as on the clean writable path.
        await using var engine = BlobDatabaseEngine.Create("probe", new());
        using var storage = BlobStorage.Create(new MemoryStream(), new MemoryStream(), new MemoryStream(), "probe");
        var legacy = new byte[18];
        legacy[16] = 1;
        using (var transaction = storage.BeginTransaction())
        {
            storage.InsertEntry(transaction, legacy);
            transaction.Commit();
        }

        // Act.
        Should.Throw<BlobCatalogException>(() => new BlobDatabase("probe", engine, storage, recover: true));
        var coordinator = storage.OnOffline!.Target.ShouldBeOfType<TransactionCoordinator>();
        try
        {
            // Assert: the existing offline callback retains the exact failed-open coordinator.
            await Should.ThrowAsync<ObjectDisposedException>(async () => await coordinator.BeginAsync(IsolationLevel.Snapshot, CancellationToken.None));
        }
        finally { await coordinator.DisposeAsync(); }
    }
}
