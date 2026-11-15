using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel.Hosting;
using Assimalign.Cohesion.Database.Blob;
using Assimalign.Cohesion.Database.Documents;
using Assimalign.Cohesion.Database.Graph;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Web.Health;

namespace Assimalign.Cohesion.Database.Hosting.ApplicationModel.Tests;

[Collection(DatabaseHealthCollection.Name)]
public sealed class DatabaseHealthOrderingTests
{
    [Fact(DisplayName = "Cohesion Test [Database.Hosting.ApplicationModel] - Escaped custom names have contributor and HTTP status parity (#1790)")]
    public async Task CheckAsync_WithEscapedCustomName_ShouldMatchAggregate()
    {
        // Arrange
        await using var engine = new HealthIdentityEngine();
        var builder = DatabaseApplication.CreateBuilder();
        builder.AddEngine(engine);
        await using var application = builder.Build();
        var resource = new Internal.DatabaseResource("health", null, [], application.Context);
        var contributor = resource.HealthContributors.Single(value => value.Name == "database/custom/tenant%201");

        // Act
        var contribution = await contributor.CheckAsync(CancellationToken.None);
        var aggregate = await CheckContextAsync(application.Context, CancellationToken.None);

        // Assert
        contribution.Status.ShouldBe(ApplicationInstanceHealthStatus.Healthy);
        ((int)contribution.Status).ShouldBe((int)aggregate.Status);
        contribution.Data.ShouldNotBeNull()["database"].ShouldBe("tenant 1");
    }

    [Theory(DisplayName = "Cohesion Test [Database.Hosting.ApplicationModel] - HTTP provisioning and pause detail keeps model name order (#1790)")]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    public async Task CheckAsync_WithReverseCreatedDatabases_ShouldReportNameOrder(int model, bool paused)
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = model switch
        {
            0 => (DatabaseEngine)DocumentDatabaseEngine.CreateBuilder("model").Build(),
            1 => GraphDatabaseEngine.CreateBuilder("model").Build(),
            _ => BlobDatabaseEngine.CreateBuilder("model").Build(),
        };
        var zulu = await engine.CreateDatabaseAsync("zulu", guard.Token);
        var alpha = await engine.CreateDatabaseAsync("alpha", guard.Token);
        var zuluStorage = DataStorage(zulu);
        var alphaStorage = DataStorage(alpha);
        var progress = new DatabaseProvisioningProgress("SchemaHeld", "items", 1, 1, false, failureCode: "CS03405");
        SetProvisioningProgress(zulu, progress);
        SetProvisioningProgress(alpha, progress);
        var builder = new DatabaseApplicationBuilder(new DatabaseApplicationOptions { ReopenOfflineDatabases = false });
        builder.AddEngine(engine);
        await using var application = builder.Build(guard.Token);
        using var zuluBracket = zuluStorage.BeginTransaction("OutsideTest");
        using var alphaBracket = alphaStorage.BeginTransaction("OutsideTest");
        if (paused)
        {
            TransactionCheckpointInterlock.ForStorage(zuluStorage).JournalSizeLimit = 2;
            TransactionCheckpointInterlock.ForStorage(alphaStorage).JournalSizeLimit = 2;
            zuluStorage.CheckpointJournalSize = 1;
            alphaStorage.CheckpointJournalSize = 1;
            TransactionCheckpointInterlock.ForStorage(zuluStorage).ObserveAdmissionPause().ShouldBeTrue();
            TransactionCheckpointInterlock.ForStorage(alphaStorage).ObserveAdmissionPause().ShouldBeTrue();
        }

        // Act
        var result = await CheckContextAsync(application.Context, guard.Token);
        zuluBracket.Rollback();
        alphaBracket.Rollback();

        // Assert
        result.Status.ShouldBe(HealthStatus.Degraded);
        var data = result.Data.ShouldNotBeNull();
        data["engine.0.provisioning.0.database"].ShouldBe("alpha");
        data["engine.0.provisioning.1.database"].ShouldBe("zulu");
        if (paused) { data["journalAdmissionPausedDatabases"].ShouldBe("model/alpha, model/zulu"); }
        else { result.Description.ShouldNotBeNull().IndexOf("model/alpha", StringComparison.Ordinal)
            .ShouldBeLessThan(result.Description.IndexOf("model/zulu", StringComparison.Ordinal)); }
    }

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "CheckAsync")]
    private static extern ValueTask<HealthCheckResult> CheckContextAsync(DatabaseApplicationContext context, CancellationToken cancellationToken);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "SetProvisioningProgress")]
    private static extern void SetProvisioningProgress(DatabaseInstance database, DatabaseProvisioningProgress? progress);

    private static Storage.Storage DataStorage(DatabaseInstance database) => database switch
    {
        DocumentDatabase documents => GetDataStorage(documents),
        GraphDatabase graph => GetDataStorage(graph),
        _ => GetDataStorage((BlobDatabase)database),
    };

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_DataStorage")]
    private static extern Documents.Storage.DocumentStorage GetDataStorage(DocumentDatabase database);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_DataStorage")]
    private static extern Graph.Storage.GraphStorage GetDataStorage(GraphDatabase database);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_DataStorage")]
    private static extern Blob.Storage.BlobStorage GetDataStorage(BlobDatabase database);
}
