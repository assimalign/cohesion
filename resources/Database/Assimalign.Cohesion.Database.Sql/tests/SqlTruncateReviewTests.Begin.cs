using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.InMemory;
using Assimalign.Cohesion.Connections.Tcp;
using Assimalign.Cohesion.Database.Client;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Client;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlTruncateReviewTests
{
    [Fact(DisplayName = "Cohesion Test [Sql] - BEGIN: offline catalog capture preserves the original failure without rollback (#1716)")]
    public async Task BeginTransactionAsync_OfflineCaptureFailure_ShouldPreserveOriginalFailure()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = SqlDatabaseEngine.Create("truncate-begin-offline", new());
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        var original = new InvalidOperationException("injected catalog capture failure");
        int captures = 0;
        SqlQueryExecutor.BeforeCatalogCapture = () =>
        {
            captures++;
            database.DataStorage.TakeOffline(StorageOfflineCause.CheckpointFailures, "injected offline capture", new IOException("device failure"));
            throw original;
        };
        try
        {
            // Act
            var failure = await Should.ThrowAsync<Exception>(async () => await session.BeginTransactionAsync(timeout.Token));

            // Assert
            failure.ShouldBeSameAs(original);
            captures.ShouldBe(1);
            database.IsOffline.ShouldBeTrue();
            database.Coordinator.GetOpenContexts().ShouldHaveSingleItem().State.ShouldBe(TransactionState.Active);
        }
        finally { SqlQueryExecutor.BeforeCatalogCapture = null; }
    }

}
