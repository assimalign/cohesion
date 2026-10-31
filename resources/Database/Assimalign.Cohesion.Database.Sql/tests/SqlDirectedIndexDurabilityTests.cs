using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed class SqlDirectedIndexDurabilityTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - Directed indexes: durable create/build/publish/drop images retain symmetric keys and metadata (#1631)")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task ExecuteAsync_DurableStage_ShouldRecoverDirectedMetadataAndRanges(int stage)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var storage = new CrashCaptureSqlStorageStrategy();
        await using var engine = await Builder(storage).BuildAsync(timeout.Token);
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        await session.ExecuteAsync("CREATE TABLE t(id INT,k INT,active BOOLEAN)", cancellationToken: timeout.Token);
        await session.ExecuteAsync("INSERT INTO t VALUES(1,7,TRUE),(2,8,TRUE),(3,NULL,TRUE),(4,NULL,TRUE),(5,9,FALSE)," +
            string.Join(',', Enumerable.Range(100, 160).Select(i => "(" + i + "," + i + ",TRUE)")), cancellationToken: timeout.Token);
        CrashCaptureSqlStorageStrategy? image = null;
        session.ProvisioningProgress = (processed, _) =>
        {
            if (image is null && (stage == 0 && processed == 0 || stage == 1 && processed > 0))
            { image = storage.CaptureDurableImages(); }
        };

        // Act
        await session.ExecuteAsync("CREATE UNIQUE INDEX ix ON t(k DESC NULLS LAST,id NULLS FIRST) WHERE active", cancellationToken: timeout.Token);
        session.ProvisioningProgress = null;
        if (stage == 3) { await session.ExecuteAsync("DROP INDEX ix ON t", cancellationToken: timeout.Token); }
        image ??= storage.CaptureDurableImages();
        await using var reopened = await Builder(image).BuildAsync(timeout.Token);
        var recovered = await reopened.OpenDatabaseAsync("db", timeout.Token);
        await using var reader = await recovered.CreateSessionAsync(timeout.Token);

        // Assert
        recovered.Catalog.RecordSpaceFormatVersion.ShouldBe(33);
        recovered.Catalog.TryGetTable("dbo", "t", out var table).ShouldBeTrue();
        recovered.Catalog.TryGetIndex(table.ObjectId, "ix", out var index).ShouldBe(stage == 2);
        if (stage == 2)
        {
            index.KeyOrders.ShouldBe([SqlCatalogIndexKeyOrder.DescendingNullsLast, SqlCatalogIndexKeyOrder.AscendingNullsFirst]);
            index.Predicate.ShouldBe("active");
            // SQ-P1 partial indexes are deliberately not candidates until SQ-P2.
            await reader.ExecuteAsync("CREATE INDEX full_ix ON t(k DESC NULLS FIRST,id DESC NULLS LAST)", cancellationToken: timeout.Token);
            (await SqlPartialIndexTests.Rows(reader, "SELECT id FROM t WHERE k>=7 AND k<9 ORDER BY id")).Select(row => row[0]).ShouldBe([1, 2]);
            reader.LastStatementMetrics!.AccessPath.ShouldBe("seek:full_ix");
            (await SqlPartialIndexTests.Rows(reader, "SELECT DIRECTION,NULL_PLACEMENT FROM INFORMATION_SCHEMA.INDEXES WHERE INDEX_NAME='ix' ORDER BY ORDINAL_POSITION"))
                .ShouldBe(new object?[][] { ["DESC", "LAST"], [null, "FIRST"] });
            await reader.ExecuteAsync("ALTER TABLE t RENAME COLUMN k TO renamed", cancellationToken: timeout.Token);
            recovered.Catalog.TryGetIndex(table.ObjectId, "ix", out var renamed).ShouldBeTrue();
            renamed.KeyOrders.ShouldBe(index.KeyOrders);
        }
        else { recovered.IndexManager.TryGetIndex(table.ObjectId, "ix", out _).ShouldBeFalse(); }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Directed indexes: format 31 is refused before data recovery (#1631)")]
    public async Task OpenDatabaseAsync_Format31_ShouldRefuseWithExistingCode()
    {
        // Arrange
        var storage = new CrashCaptureSqlStorageStrategy();
        await using var engine = await Builder(storage).BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await database.Catalog.SetRecordSpaceFormatVersionAsync(31, CancellationToken.None);
        await using var reopened = await Builder(storage.CaptureDurableImages()).BuildAsync(CancellationToken.None);

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await reopened.OpenDatabaseAsync("db", CancellationToken.None));

        // Assert
        failure.Diagnostics.ShouldContain(d => d.Code == "CS06041");
        failure.Message.ShouldContain("supports only format 33", Case.Sensitive);
    }

    private static SqlDatabaseEngineBuilder Builder(CrashCaptureSqlStorageStrategy storage)
    {
        var builder = SqlDatabaseEngine.CreateBuilder("directed-crash");
        builder.Options.StorageStrategy = storage;
        return builder;
    }
}
