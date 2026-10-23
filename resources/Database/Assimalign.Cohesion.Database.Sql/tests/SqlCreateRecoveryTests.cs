using System;
using System.IO;
using System.Linq;
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
        refusal.Message.ShouldContain("restore the complete database", Case.Sensitive);
        refusal.Message.ShouldNotContain("DropDatabaseAsync", Case.Sensitive);
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
        var failure = await Should.ThrowAsync<DatabaseException>(() => first.CreateDatabaseAsync("sales", CancellationToken.None).AsTask());
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS06041");
        failure.InnerException.ShouldBeOfType<IOException>().Message.ShouldBe("Injected create interruption.");
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

    [Theory(DisplayName = "Cohesion Test [Database.Sql] - Interrupted create: a retry after dbo uses the requested collation (#1158)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateDatabaseAsync_AfterDbo_ShouldApplyRequestedCollation(bool initiallyInsensitive)
    {
        // Arrange
        var image = await CaptureCreationAsync(3, initiallyInsensitive ? Collation.CaseInsensitive : Collation.Binary);
        await using var engine = SqlDatabaseEngine.Create("collation-retry", new() { StorageStrategy = image });
        var requested = initiallyInsensitive ? Collation.Binary : Collation.CaseInsensitive;

        // Act
        var database = await engine.CreateDatabaseAsync("sales", requested, CancellationToken.None);

        // Assert
        database.Catalog.DefaultCollation.ShouldBe(requested);
        database.Catalog.Namespaces.ShouldHaveSingleItem().Name.ShouldBe("dbo");
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql] - Interrupted create: repeated collation retries remain recoverable (#1158)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateDatabaseAsync_RepeatedCollationInterruptions_ShouldUseLatestRequestedOptions(bool initiallyInsensitive)
    {
        // Arrange: each option change committed, but creation still has not stamped its marker.
        var initial = initiallyInsensitive ? Collation.CaseInsensitive : Collation.Binary;
        var requested = initiallyInsensitive ? Collation.Binary : Collation.CaseInsensitive;
        var image = await CaptureCreationAsync(3, initial);
        CrashCaptureSqlStorageStrategy interrupted;
        using (var catalogStorage = image.OpenStorage("sales", catalog: true))
        {
            SqlCatalog.Open(catalogStorage, requested);
            SqlCatalog.Open(catalogStorage, initial);
            interrupted = image.CaptureDurableImages();
        }
        await using var engine = SqlDatabaseEngine.Create("collation-retries", new() { StorageStrategy = interrupted });

        // Act
        var database = await engine.CreateDatabaseAsync("sales", requested, CancellationToken.None);

        // Assert
        database.Catalog.DefaultCollation.ShouldBe(requested);
        database.Catalog.RecordSpaceFormatVersion.ShouldBe(SqlRowCodec.RecordSpaceFormatVersion);
        interrupted.IsCreationPending("sales").ShouldBeFalse();
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql] - Interrupted create: pre-header files recover only through create or application Build (#1158)")]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(8191, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(8191, true)]
    public async Task CreateDatabaseAsync_PreHeaderFiles_ShouldRestartWithoutUncodedIoFailure(int length, bool catalog)
    {
        // Arrange
        string root = Path.Combine(Path.GetTempPath(), "cohesion-preheader", Guid.NewGuid().ToString("N"));
        string directory = Path.Combine(root, "sales");
        Directory.CreateDirectory(directory);
        try
        {
            if (catalog)
            {
                // Take the actual data-create crash image, before close checkpoints it.
                var image = new CrashCaptureSqlStorageStrategy();
                using var data = image.CreateStorage("sales", catalog: false);
                var bytes = image.GetDurableImage("sales");
                File.WriteAllBytes(Path.Combine(directory, "sql.dat"), bytes.Data);
                File.WriteAllBytes(Path.Combine(directory, "sql.log"), bytes.Journal);
                File.WriteAllBytes(Path.Combine(directory, "sql.bak"), bytes.Backup);
                File.WriteAllText(Path.Combine(directory, "sql.create"), "COHSQLCREATE1\n");
            }
            string partial = Path.Combine(directory, catalog ? "catalog.dat" : "sql.dat");
            File.WriteAllBytes(partial, new byte[length]);
            await using var engine = SqlDatabaseEngine.Create("preheader", new() { RootPath = root });

            // Act
            var refusal = await Should.ThrowAsync<DatabaseNotFoundException>(() => engine.OpenDatabaseAsync("sales", CancellationToken.None).AsTask());
            File.ReadAllBytes(partial).Length.ShouldBe(length);
            var database = await engine.CreateDatabaseAsync("sales", Collation.CaseInsensitive, CancellationToken.None);

            // Assert
            refusal.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS03401");
            refusal.Message.ShouldContain("CreateDatabaseAsync", Case.Sensitive);
            database.Catalog.DefaultCollation.ShouldBe(Collation.CaseInsensitive);
            database.Catalog.Namespaces.ShouldHaveSingleItem().Name.ShouldBe("dbo");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql] - Missing catalog: acknowledged empty-table DDL is never mistaken for creation (#1158)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OpenDatabaseAsync_MissingCatalogAfterEmptyTableCommit_ShouldRefuseUnchangedFiles(bool staleClaim)
    {
        // Arrange: DDL can commit while every user data page is still empty.
        string root = Path.Combine(Path.GetTempPath(), "cohesion-empty-ddl", Guid.NewGuid().ToString("N"));
        string directory = Path.Combine(root, "sales");
        try
        {
            var images = new System.Collections.Generic.Dictionary<string, byte[]>();
            await using (var creator = SqlDatabaseEngine.Create("ddl", new() { RootPath = root }))
            {
                var database = await creator.CreateDatabaseAsync("sales", CancellationToken.None);
                await using var session = await database.CreateSessionAsync(CancellationToken.None);
                var result = await session.ExecuteAsync("CREATE TABLE acknowledged(id INT)", cancellationToken: CancellationToken.None);
                result.Status.ShouldBe(Assimalign.Cohesion.Database.Execution.DatabaseResultStatus.Success);
                foreach (string extension in new[] { "dat", "log", "bak" })
                {
                    string path = Path.Combine(directory, "sql." + extension);
                    using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var copy = new MemoryStream();
                    source.CopyTo(copy);
                    images[path] = copy.ToArray();
                }
            }
            foreach (var image in images) { File.WriteAllBytes(image.Key, image.Value); }
            foreach (string extension in new[] { "dat", "log", "bak" }) { File.Delete(Path.Combine(directory, "catalog." + extension)); }
            if (staleClaim) { File.WriteAllText(Path.Combine(directory, "sql.create"), "COHSQLCREATE1\n"); }
            await using var engine = SqlDatabaseEngine.Create("refuse-ddl", new() { RootPath = root });

            // Act
            var refusal = await Should.ThrowAsync<DatabaseException>(() => engine.OpenDatabaseAsync("sales", CancellationToken.None).AsTask());

            // Assert
            refusal.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS03401");
            File.Exists(Path.Combine(directory, "catalog.dat")).ShouldBeFalse();
            foreach (var image in images) { File.ReadAllBytes(image.Key).ShouldBe(image.Value); }
        }
        finally { if (Directory.Exists(root)) { Directory.Delete(root, recursive: true); } }
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql] - Short data beside other state: preserve files without a create retry loop (#1158)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OpenDatabaseAsync_ShortDataWithOtherState_ShouldGiveAccuratePreservationAdvice(bool catalogExists)
    {
        // Arrange
        string root = Path.Combine(Path.GetTempPath(), "cohesion-conflicting-create", Guid.NewGuid().ToString("N"));
        string directory = Path.Combine(root, "sales");
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllBytes(Path.Combine(directory, "sql.dat"), [1]);
            if (catalogExists) { File.WriteAllBytes(Path.Combine(directory, "catalog.dat"), [2]); }
            else { File.WriteAllBytes(Path.Combine(directory, "sql.log"), [3]); }
            var before = Directory.GetFiles(directory).ToDictionary(path => Path.GetFileName(path)!, File.ReadAllBytes);
            await using var engine = SqlDatabaseEngine.Create("conflict", new() { RootPath = root });

            // Act
            var refusal = await Should.ThrowAsync<SqlDataStorageFormatException>(() => engine.OpenDatabaseAsync("sales", CancellationToken.None).AsTask());
            var create = await Should.ThrowAsync<SqlDataStorageFormatException>(() => engine.CreateDatabaseAsync("sales", CancellationToken.None).AsTask());

            // Assert: create is not a remedy for this persisted state.
            refusal.Message.ShouldBe(create.Message);
            refusal.Message.ShouldContain("Preserve all files", Case.Sensitive);
            refusal.Message.ShouldContain("cannot safely restart", Case.Sensitive);
            refusal.Message.ShouldNotContain("Retry");
            refusal.Message.ShouldNotContain("restore");
            refusal.Message.ShouldNotContain("drop");
            Directory.GetFiles(directory).Select(Path.GetFileName).Order().ShouldBe(before.Keys.Order());
            foreach (var file in before) { File.ReadAllBytes(Path.Combine(directory, file.Key!)).ShouldBe(file.Value); }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Creation probe: transient I/O asks for retry without restore or drop advice (#1158)")]
    public async Task OpenDatabaseAsync_DataProbeBufferPoolFull_ShouldGiveRetryAdvice()
    {
        // Arrange
        var image = await CaptureCreationAsync(3);
        image.DataProbeFailure = new Assimalign.Cohesion.Database.Storage.StorageIOException("Buffer pool is full and all pages are pinned.");
        await using var engine = SqlDatabaseEngine.Create("transient-probe", new() { StorageStrategy = image });

        // Act
        var refusal = await Should.ThrowAsync<DatabaseException>(() => engine.OpenDatabaseAsync("sales", CancellationToken.None).AsTask());
        image.DataProbeFailure = null;
        var recovered = await engine.OpenDatabaseAsync("sales", CancellationToken.None);

        // Assert
        refusal.Message.ShouldContain("Retry OpenDatabaseAsync", Case.Sensitive);
        refusal.Message.ShouldNotContain("restore");
        refusal.Message.ShouldNotContain("drop");
        recovered.Catalog.RecordSpaceFormatVersion.ShouldBe(SqlRowCodec.RecordSpaceFormatVersion);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql] - Creation probe: wrapped corruption refuses adoption consistently")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task OpenDatabaseAsync_DataProbeCorruptionWrapped_ShouldRefuseIncompleteCreation(int wrapper)
    {
        // Arrange
        var image = await CaptureCreationAsync(3);
        var corruption = new Assimalign.Cohesion.Database.Storage.StorageCorruptionException((Assimalign.Cohesion.Database.Storage.PageId)1L, "Damaged data probe.");
        image.DataProbeFailure = wrapper switch
        {
            1 => new DatabaseException("Coded probe failure.", corruption),
            2 => new AggregateException(new InvalidOperationException("Cleanup failed."), corruption),
            3 => new IOException("Read failed.", corruption),
            _ => corruption,
        };
        await using var engine = SqlDatabaseEngine.Create("corrupt-probe", new() { StorageStrategy = image });

        // Act
        var refusal = await Should.ThrowAsync<SqlDataStorageFormatException>(() => engine.OpenDatabaseAsync("sales", CancellationToken.None).AsTask());
        image.DataProbeFailure = null;
        var recovered = await engine.OpenDatabaseAsync("sales", CancellationToken.None);

        // Assert
        refusal.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "CS03401");
        refusal.Message.ShouldContain("restore the complete database", Case.Sensitive);
        recovered.Catalog.RecordSpaceFormatVersion.ShouldBe(SqlRowCodec.RecordSpaceFormatVersion);
    }

    private static async Task<CrashCaptureSqlStorageStrategy> CaptureCreationAsync(int step, Collation? collation = null)
    {
        var strategy = new CrashCaptureSqlStorageStrategy();
        using var data = strategy.CreateStorage("sales", catalog: false);
        if (step == 0) { return strategy.CaptureDurableImages(); }
        using var catalogStorage = strategy.CreateStorage("sales", catalog: true);
        if (step == 1) { return strategy.CaptureDurableImages(); }
        var catalog = SqlCatalog.Open(catalogStorage, collation ?? Collation.CaseInsensitive);
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
