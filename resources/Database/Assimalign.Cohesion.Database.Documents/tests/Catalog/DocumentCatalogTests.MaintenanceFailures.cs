using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Documents.Tests.TestObjects;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Documents.Catalog.Tests;

public sealed partial class DocumentCatalogTests
{
    [Theory(DisplayName = "Cohesion Test [Documents] - Purge: history maintenance preserves an earlier purge failure")]
    [InlineData("success")]
    [InlineData("io")]
    [InlineData("busy")]
    [InlineData("cancellation")]
    [InlineData("disposal")]
    public async Task RunVersionPurgePass_HistoryMaintenanceFails_ShouldPreserveEveryFailure(string kind)
    {
        // Arrange
        await using var database = new CatalogHistoryDatabase();
        var writer = await database.Coordinator.BeginAsync(IsolationLevel.Snapshot, CancellationToken.None);
        await database.Catalog.SaveDocumentAsync(new DocumentCatalogEntry(Guid.NewGuid(), "pending", 1, 1, 2, 0), writer);
        database.FailUndo = kind != "success";
        Exception? purgeFailure = kind switch
        {
            "io" => new IOException("Injected catalog undo failure."),
            "busy" => new StorageTransactionException("Page 1 is write-locked by transaction 9."),
            "cancellation" => new OperationCanceledException("Injected purge cancellation."),
            "disposal" => new ObjectDisposedException("purge storage"),
            _ => null,
        };
        database.UndoFailure = purgeFailure;
        await database.Coordinator.RollbackAsync(writer, CancellationToken.None);
        var maintenanceFailure = new IOException("Injected history maintenance failure.");
        database.Catalog.BeforeHistoryMaintenance = () => throw maintenanceFailure;
        try
        {
            // Act / Assert
            if (purgeFailure is not null)
            {
                var error = Should.Throw<Exception>(() => database.Catalog.RunVersionPurgePass(CancellationToken.None));
                error.ShouldBeSameAs(purgeFailure);
                error.Data["HistoryMaintenanceFailure"].ShouldBeSameAs(maintenanceFailure);
            }
            else
            {
                Should.Throw<IOException>(() => database.Catalog.RunVersionPurgePass(CancellationToken.None)).ShouldBeSameAs(maintenanceFailure);
            }
        }
        finally
        {
            database.Catalog.BeforeHistoryMaintenance = null;
            database.FailUndo = false;
        }
        database.Catalog.RunVersionPurgePass(CancellationToken.None);
        database.Coordinator.VersionStore.PendingAbortedPurges.ShouldBeEmpty();
    }
}
