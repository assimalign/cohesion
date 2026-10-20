using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed class SqlCreateRecoveryTests
{
    [Theory(DisplayName = "Cohesion Test [Database.Sql] - Interrupted create: a record in the initial data page is never adopted (#1158)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OpenDatabaseAsync_OrphanedDataRecord_ShouldRefuseWithoutStamping(bool catalogExists)
    {
        // Arrange: page count alone cannot prove the initial shared data page empty.
        var strategy = new CrashCaptureSqlStorageStrategy();
        using var data = strategy.CreateStorage("sales", catalog: false);
        data.InsertRow([1, 2, 3]);
        if (catalogExists)
        {
            using var catalog = strategy.CreateStorage("sales", catalog: true);
        }
        var crashed = strategy.CaptureDurableImages();
        await using var engine = SqlDatabaseEngine.Create("orphaned", new SqlDatabaseEngineOptions { StorageStrategy = crashed });

        // Act
        var refusal = await Should.ThrowAsync<DatabaseException>(() => engine.OpenDatabaseAsync("sales", CancellationToken.None).AsTask());

        // Assert
        refusal.Message.ShouldContain("drop the database (DropDatabaseAsync)", Case.Sensitive);
        if (catalogExists)
        {
            using var catalogStorage = crashed.OpenStorage("sales", catalog: true);
            SqlCatalog.Open(catalogStorage).RecordSpaceFormatVersion.ShouldBe(1);
        }
        else
        {
            crashed.StorageExists("sales", catalog: true).ShouldBeFalse();
        }
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql] - Interrupted create: a fault after either real file-set creation recovers (#1158)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateDatabaseAsync_StorageCreationInterrupted_ShouldRecoverCrashImage(bool catalog)
    {
        // Arrange
        var strategy = new CrashCaptureSqlStorageStrategy();
        CrashCaptureSqlStorageStrategy? crashed = null;
        strategy.StorageCreated = isCatalog =>
        {
            if (isCatalog == catalog)
            {
                crashed = strategy.CaptureDurableImages();
                throw new IOException("Injected create interruption.");
            }
        };
        await using var first = SqlDatabaseEngine.Create("interrupted", new SqlDatabaseEngineOptions { StorageStrategy = strategy });

        // Act
        await Should.ThrowAsync<IOException>(() => first.CreateDatabaseAsync("sales", CancellationToken.None).AsTask());
        await using var next = SqlDatabaseEngine.Create("next", new SqlDatabaseEngineOptions { StorageStrategy = crashed.ShouldNotBeNull() });
        var recovered = await next.CreateDatabaseAsync("sales", CancellationToken.None);

        // Assert
        recovered.Catalog.RecordSpaceFormatVersion.ShouldBe(SqlRowCodec.RecordSpaceFormatVersion);
        recovered.Catalog.Namespaces.ShouldHaveSingleItem().Name.ShouldBe("dbo");
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql] - Interrupted create: open completes every durable creation boundary (#1158)")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task OpenDatabaseAsync_InterruptedCreate_ShouldCompleteAndReopen(int step)
    {
        // Arrange: retain the crash image before orderly disposal changes either file set.
        var crashed = await CaptureCreationAsync(step);

        // Act
        await using (var engine = SqlDatabaseEngine.Create("recover", new SqlDatabaseEngineOptions { StorageStrategy = crashed }))
        {
            var database = await engine.OpenDatabaseAsync("sales", CancellationToken.None);

            // Assert
            database.Catalog.RecordSpaceFormatVersion.ShouldBe(SqlRowCodec.RecordSpaceFormatVersion);
            database.Catalog.Namespaces.ShouldHaveSingleItem().Name.ShouldBe("dbo");
            database.Catalog.DefaultCollation.ShouldBe(step >= 2 ? Collation.CaseInsensitive : Collation.Binary);
            await using var session = await database.CreateSessionAsync(CancellationToken.None);
            await session.ExecuteAsync("CREATE TABLE orders (id INT PRIMARY KEY)", cancellationToken: CancellationToken.None);
            await session.ExecuteAsync("INSERT INTO orders VALUES (1)", cancellationToken: CancellationToken.None);
        }
        await using var reopened = SqlDatabaseEngine.Create("reopen", new SqlDatabaseEngineOptions { StorageStrategy = crashed.CaptureDurableImages() });
        (await reopened.OpenDatabaseAsync("SALES", CancellationToken.None)).Catalog.Tables.ShouldHaveSingleItem().Name.ShouldBe("orders");
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql] - Interrupted create: explicit create completes empty storage and preserves committed collation (#1158)")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task CreateDatabaseAsync_InterruptedCreate_ShouldCompleteWithoutDropping(int step)
    {
        // Arrange
        var crashed = await CaptureCreationAsync(step);
        await using var engine = SqlDatabaseEngine.Create("recover", new SqlDatabaseEngineOptions { StorageStrategy = crashed });

        // Act
        var database = await engine.CreateDatabaseAsync("SALES", Collation.CaseInsensitive, CancellationToken.None);

        // Assert
        database.Name.ToString().ShouldBe("sales");
        database.Catalog.RecordSpaceFormatVersion.ShouldBe(SqlRowCodec.RecordSpaceFormatVersion);
        database.Catalog.Namespaces.ShouldHaveSingleItem().Name.ShouldBe("dbo");
        database.Catalog.DefaultCollation.ShouldBe(Collation.CaseInsensitive);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql] - Interrupted create: application Build recovers before applying the declaration (#1158)")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task Build_InterruptedCreate_ShouldRecoverAndProvisionBeforeReturning(int step)
    {
        // Arrange
        var crashed = await CaptureCreationAsync(step);
        var builder = Assimalign.Cohesion.Database.Hosting.DatabaseApplication.CreateBuilder();
        builder.AddSql("recover", sql =>
        {
            sql.Options.StorageStrategy = crashed;
            sql.AddDatabase("sales", database =>
            {
                database.DefaultCollation = Collation.CaseInsensitive;
                database.Script("seed", "CREATE TABLE orders (id INT PRIMARY KEY)");
            });
        });

        // Act
        await using var application = builder.Build(CancellationToken.None);

        // Assert
        var engine = application.Context.GetEngine("recover").ShouldBeOfType<SqlDatabaseEngine>();
        engine.TryGetDatabase("sales", out SqlDatabase? database).ShouldBeTrue();
        database!.Catalog.RecordSpaceFormatVersion.ShouldBe(SqlRowCodec.RecordSpaceFormatVersion);
        database.Catalog.DefaultCollation.ShouldBe(Collation.CaseInsensitive);
        database.Catalog.Tables.ShouldHaveSingleItem().Name.ShouldBe("orders");
    }

    private static async Task<CrashCaptureSqlStorageStrategy> CaptureCreationAsync(int step)
    {
        var strategy = new CrashCaptureSqlStorageStrategy();
        using var data = strategy.CreateStorage("sales", catalog: false);
        if (step == 0) { return strategy.CaptureDurableImages(); }
        using var catalogStorage = strategy.CreateStorage("sales", catalog: true);
        if (step == 1) { return strategy.CaptureDurableImages(); }
        var catalog = SqlCatalog.Open(catalogStorage, Collation.CaseInsensitive);
        if (step >= 3)
        {
            await catalog.CreateSchemaAsync(new SqlCatalogNamespace("dbo"), CancellationToken.None);
        }
        if (step == 4)
        {
            await catalog.SetRecordSpaceFormatVersionAsync(SqlRowCodec.RecordSpaceFormatVersion, CancellationToken.None);
        }
        return strategy.CaptureDurableImages();
    }
}
