using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlDefinedFunctionTests
{
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Build refuses completed receipts for different declared objects")]
    [InlineData(SqlProvisioningMode.Apply, "CREATE TABLE wanted (id INT)")]
    [InlineData(SqlProvisioningMode.Verify, "CREATE TABLE wanted (id INT)")]
    [InlineData(SqlProvisioningMode.Apply, "ALTER TABLE wanted ADD COLUMN extra INT")]
    [InlineData(SqlProvisioningMode.Verify, "DROP TABLE wanted")]
    [InlineData(SqlProvisioningMode.Apply, "CREATE INDEX wanted ON unrelated(id)")]
    [InlineData(SqlProvisioningMode.Verify, "DROP INDEX wanted ON unrelated")]
    [InlineData(SqlProvisioningMode.Apply, "CREATE FUNCTION wanted(p INT) RETURNS INT RETURN p")]
    [InlineData(SqlProvisioningMode.Verify, "DROP FUNCTION wanted(INTEGER)")]
    [InlineData(SqlProvisioningMode.Apply, "CREATE DOMAIN wanted AS INT")]
    [InlineData(SqlProvisioningMode.Verify, "DROP DOMAIN wanted")]
    public async Task BuildAsync_MismatchedReceiptObjects_ShouldRefuseCompletedStatement(SqlProvisioningMode mode, string sql)
    {
        // Arrange: simulate an inconsistent durable receipt, bypassing publication validation.
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sql)));
        var storage = new CrashCaptureSqlStorageStrategy();
        var seed = SqlDatabaseEngine.CreateBuilder("receipt-validation");
        seed.Options.StorageStrategy = storage;
        await using (var engine = await seed.BuildAsync(CancellationToken.None))
        {
            await using var session = await SessionAsync(engine);
            await ExecuteAsync(session, "CREATE TABLE unrelated (id INT)");
            var database = await engine.OpenDatabaseAsync("functions", CancellationToken.None);
            ulong generation = database.Catalog.DdlGeneration;
            string json = $$"""
                {"Name":"001","ContentHash":"{{hash}}","StatementCount":1,"CompletedStatements":1,"BaseGeneration":{{generation}},"Generation":{{generation}},"SchemaHash":null,"Objects":["T:\"dbo\".\"unrelated\""],"StatementObjects":["T:\"dbo\".\"unrelated\""]}
                """;
            var record = new DatabaseKeyWriter().AppendInt32(12).AppendString(json, Collation.Binary).ToArray();
            using var transaction = database.CatalogStorage.BeginTransaction();
            database.CatalogStorage.InsertRow(transaction, record);
            transaction.Commit();
        }
        var reopen = SqlDatabaseEngine.CreateBuilder("receipt-validation");
        reopen.Options.StorageStrategy = storage.CaptureDurableImages();
        reopen.AddDatabase("functions", database =>
        {
            database.Provisioning = mode;
            database.Script("001", sql);
        });

        // Act
        var failure = await Should.ThrowAsync<SqlSchemaMigrationException>(async () =>
        {
            await using var engine = await reopen.BuildAsync(CancellationToken.None);
        });

        // Assert
        failure.Message.ShouldContain("COHSQLS001");
        failure.Message.ShouldContain("001");
        failure.Message.ShouldContain("wanted");
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Cold receipt validation preserves completed drops and domain function signatures")]
    [InlineData(SqlProvisioningMode.Apply)]
    [InlineData(SqlProvisioningMode.Verify)]
    public async Task BuildAsync_CompletedObjectLifecycle_ShouldValidateHistoricalTargets(SqlProvisioningMode mode)
    {
        // Arrange: every script-created object is gone, including the function parameter's domain.
        const string sql = "CREATE DOMAIN units AS INT; CREATE FUNCTION project(p units) RETURNS INT RETURN p; " +
            "DROP FUNCTION project(INTEGER); DROP DOMAIN units; CREATE TABLE temporary_rows(id INT); " +
            "CREATE INDEX ix_temp ON temporary_rows(id); ALTER TABLE temporary_rows ADD COLUMN extra INT; " +
            "DROP INDEX ix_temp ON temporary_rows; DROP TABLE temporary_rows";
        var storage = new CrashCaptureSqlStorageStrategy();
        await using (var first = await Builder(storage, SqlProvisioningMode.Apply).BuildAsync(CancellationToken.None))
        {
            var database = await first.OpenDatabaseAsync("functions", CancellationToken.None);
            await using var session = await database.CreateSessionAsync(CancellationToken.None);
            // Force the receipt walk without changing the script's checksum.
            await ExecuteAsync(session, "CREATE TABLE sentinel(id INT)");
        }

        // Act
        await using var reopened = await Builder(storage.CaptureDurableImages(), mode).BuildAsync(CancellationToken.None);
        var result = await reopened.OpenDatabaseAsync("functions", CancellationToken.None);

        // Assert
        result.Catalog.Tables.ShouldHaveSingleItem().Name.ShouldBe("sentinel");
        result.Catalog.Functions.ShouldBeEmpty();
        result.Catalog.Domains.ShouldBeEmpty();
        var receipt = result.Catalog.Scripts.ShouldHaveSingleItem();
        receipt.CompletedStatements.ShouldBe(9);
        receipt.StatementObjects[1].ShouldBe(receipt.StatementObjects[2]);
        receipt.Objects.Count.ShouldBe(2);

        static SqlDatabaseEngineBuilder Builder(CrashCaptureSqlStorageStrategy strategy, SqlProvisioningMode provisioning)
        {
            var builder = SqlDatabaseEngine.CreateBuilder("receipt-lifecycle");
            builder.Options.StorageStrategy = strategy;
            builder.AddDatabase("functions", database =>
            {
                database.Provisioning = provisioning;
                database.Script("001", sql);
            });
            return builder;
        }
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - A declared UNIQUE constraint drop records its index target")]
    public async Task BuildAsync_DropUniqueConstraint_ShouldRecordActualIndexTarget()
    {
        // Arrange
        const string sql = "CREATE TABLE script_rows(id INT, CONSTRAINT uq UNIQUE(id)); ALTER TABLE script_rows DROP CONSTRAINT uq";
        var storage = new CrashCaptureSqlStorageStrategy();

        // Act
        await using (var engine = await ScriptBuilder(storage, sql: sql).BuildAsync(CancellationToken.None))
        {
            var database = await engine.OpenDatabaseAsync("functions", CancellationToken.None);
            await using var session = await database.CreateSessionAsync(CancellationToken.None);
            await ExecuteAsync(session, "INSERT INTO script_rows VALUES(1), (1)");
            await ExecuteAsync(session, "CREATE TABLE sentinel(id INT)");
        }
        await using var reopened = await ScriptBuilder(storage.CaptureDurableImages(), sql: sql).BuildAsync(CancellationToken.None);
        var result = await reopened.OpenDatabaseAsync("functions", CancellationToken.None);

        // Assert
        result.Catalog.Scripts.ShouldHaveSingleItem().StatementObjects.ShouldBe(["T:\"dbo\".\"script_rows\"", "I:\"dbo\".\"script_rows\".\"uq\""]);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Successful IF EXISTS drops record verified absence")]
    public async Task BuildAsync_AbsentIfExistsDrops_ShouldCompleteAndValidateReceipts()
    {
        // Arrange
        const string sql = "DROP TABLE IF EXISTS absent; CREATE TABLE script_rows(id INT); DROP INDEX IF EXISTS absent ON script_rows";
        var storage = new CrashCaptureSqlStorageStrategy();

        // Act
        await using (var engine = await ScriptBuilder(storage, sql: sql).BuildAsync(CancellationToken.None))
        {
            var database = await engine.OpenDatabaseAsync("functions", CancellationToken.None);
            await using var session = await database.CreateSessionAsync(CancellationToken.None);
            await ExecuteAsync(session, "CREATE TABLE sentinel(id INT)");
        }
        await using var reopened = await ScriptBuilder(storage.CaptureDurableImages(), sql: sql).BuildAsync(CancellationToken.None);
        var result = await reopened.OpenDatabaseAsync("functions", CancellationToken.None);

        // Assert
        var receipt = result.Catalog.Scripts.ShouldHaveSingleItem();
        receipt.CompletedStatements.ShouldBe(3);
        receipt.StatementObjects.ShouldBe(["T:\"dbo\".\"absent\"", "T:\"dbo\".\"script_rows\"", "I:\"dbo\".\"script_rows\".\"absent\""]);
        receipt.Objects.ShouldBe(["T:\"dbo\".\"script_rows\""]);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Historical function drops preserve equivalent domain aliases")]
    public async Task BuildAsync_DroppedDomainAlias_ShouldValidateHistoricalFunctionTarget()
    {
        // Arrange
        const string sql = "CREATE DOMAIN a AS INT; CREATE DOMAIN b AS INT; " +
            "CREATE FUNCTION project(p a) RETURNS INT RETURN p; DROP FUNCTION project(b); DROP DOMAIN a; DROP DOMAIN b";
        var storage = new CrashCaptureSqlStorageStrategy();
        await using (var engine = await ScriptBuilder(storage, sql: sql).BuildAsync(CancellationToken.None))
        {
            var database = await engine.OpenDatabaseAsync("functions", CancellationToken.None);
            await using var session = await database.CreateSessionAsync(CancellationToken.None);
            await ExecuteAsync(session, "CREATE TABLE sentinel(id INT)");
        }

        // Act
        await using var reopened = await ScriptBuilder(storage.CaptureDurableImages(), sql: sql).BuildAsync(CancellationToken.None);
        var result = await reopened.OpenDatabaseAsync("functions", CancellationToken.None);

        // Assert
        result.Catalog.Scripts.ShouldHaveSingleItem().CompletedStatements.ShouldBe(6);
        result.Catalog.Functions.ShouldBeEmpty();
        result.Catalog.Domains.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Removed native domain aliases preserve completed function receipts")]
    public async Task BuildAsync_RemovedNativeDomainAlias_ShouldValidateHistoricalFunctionTarget()
    {
        // Arrange
        const string sql = "CREATE FUNCTION project(p a) RETURNS INT RETURN p; DROP FUNCTION project(b)";
        var storage = new CrashCaptureSqlStorageStrategy();
        var seed = ScriptBuilder(storage, sql: sql);
        seed.Types.Add(SqlType.CreateDomain("a", SqlType.Integer)).Add(SqlType.CreateDomain("b", SqlType.Integer));
        await using (var engine = await seed.BuildAsync(CancellationToken.None))
        {
            var database = await engine.OpenDatabaseAsync("functions", CancellationToken.None);
            await using var session = await database.CreateSessionAsync(CancellationToken.None);
            await ExecuteAsync(session, "CREATE TABLE sentinel(id INT)");
        }

        // Act
        await using var reopened = await ScriptBuilder(storage.CaptureDurableImages(), sql: sql).BuildAsync(CancellationToken.None);
        var result = await reopened.OpenDatabaseAsync("functions", CancellationToken.None);

        // Assert
        var receipt = result.Catalog.Scripts.ShouldHaveSingleItem();
        receipt.CompletedStatements.ShouldBe(2);
        receipt.StatementParameterTypes[0].ShouldBe(["\"a\""]);
        receipt.StatementParameterTypes[1].ShouldBe(["\"b\""]);
        result.Catalog.Functions.ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Removing a historical domain declaration preserves completed function receipts")]
    [InlineData(SqlProvisioningMode.Apply)]
    [InlineData(SqlProvisioningMode.Verify)]
    public async Task BuildAsync_RemovedDomainScript_ShouldValidateRetainedFunctionReceipt(SqlProvisioningMode mode)
    {
        // Arrange
        var storage = new CrashCaptureSqlStorageStrategy();
        await using (var first = await Builder(storage, true, SqlProvisioningMode.Apply).BuildAsync(CancellationToken.None)) { }

        // Act
        await using var reopened = await Builder(storage.CaptureDurableImages(), false, mode).BuildAsync(CancellationToken.None);
        var database = await reopened.OpenDatabaseAsync("functions", CancellationToken.None);

        // Assert
        database.Catalog.Scripts.Count.ShouldBe(3);
        database.Catalog.Functions.ShouldBeEmpty();
        database.Catalog.Domains.ShouldBeEmpty();

        static SqlDatabaseEngineBuilder Builder(CrashCaptureSqlStorageStrategy strategy, bool includeDomain, SqlProvisioningMode provisioning)
        {
            var builder = SqlDatabaseEngine.CreateBuilder("receipt-removed-domain");
            builder.Options.StorageStrategy = strategy;
            builder.AddDatabase("functions", database =>
            {
                database.Provisioning = provisioning;
                if (includeDomain) { database.Script("001", "CREATE DOMAIN units AS INT"); }
                database.Script("002", "CREATE FUNCTION project(p units) RETURNS INT RETURN p");
                database.Script("003", "DROP FUNCTION project(INTEGER); DROP DOMAIN units");
            });
            return builder;
        }
    }
}
