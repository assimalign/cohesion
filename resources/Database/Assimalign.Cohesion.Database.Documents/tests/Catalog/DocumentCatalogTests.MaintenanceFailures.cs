using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Documents.Tests.TestObjects;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Documents.Catalog.Tests;

public sealed partial class DocumentCatalogTests
{
    [Theory(DisplayName = "Cohesion Test [Documents] - Purge: history maintenance preserves an earlier purge failure")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunVersionPurgePass_HistoryMaintenanceFails_ShouldPreserveEveryFailure(bool purgeFails)
    {
        // Arrange
        await using var database = new CatalogHistoryDatabase();
        var writer = await database.Coordinator.BeginAsync(IsolationLevel.Snapshot, CancellationToken.None);
        await database.Catalog.SaveDocumentAsync(new DocumentCatalogEntry(Guid.NewGuid(), "pending", 1, 1, 2, 0), writer);
        database.FailUndo = purgeFails;
        await database.Coordinator.RollbackAsync(writer, CancellationToken.None);
        var maintenanceFailure = new IOException("Injected history maintenance failure.");
        database.Catalog.BeforeHistoryMaintenance = () => throw maintenanceFailure;
        try
        {
            // Act / Assert
            if (purgeFails)
            {
                var error = Should.Throw<AggregateException>(() => database.Catalog.RunVersionPurgePass(CancellationToken.None));
                error.InnerExceptions.Count.ShouldBe(2);
                error.InnerExceptions[0].ShouldBeOfType<IOException>().Message.ShouldBe("Injected catalog undo failure.");
                error.InnerExceptions[1].ShouldBeSameAs(maintenanceFailure);
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
