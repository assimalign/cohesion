using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlDefinedFunctionTests
{
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Quoted script objects retain ownership during cold typed reconciliation")]
    public async Task BuildAsync_QuotedScriptOwnership_ShouldPreserveObjects()
    {
        // Arrange
        const string sql = "CREATE TABLE \"script.rows:one\"(id INT); CREATE INDEX \"ix.id:one\" ON \"script.rows:one\"(id)";
        var storage = new CrashCaptureSqlStorageStrategy();
        await using (var engine = await ScriptBuilder(storage, sql: sql).BuildAsync(CancellationToken.None))
        {
            var database = await engine.OpenDatabaseAsync("functions", CancellationToken.None);
            await using var session = await database.CreateSessionAsync(CancellationToken.None);
            await ExecuteAsync(session, "CREATE TABLE sentinel(id INT)");
        }

        // Act
        var reopen = ScriptBuilder(storage.CaptureDurableImages(), sql: sql);
        await using var reopened = await reopen.BuildAsync(CancellationToken.None);
        var result = await reopened.OpenDatabaseAsync("functions", CancellationToken.None);

        // Assert
        result.Catalog.TryGetTable("DBO", "SCRIPT.ROWS:ONE", out var table).ShouldBeTrue();
        result.Catalog.TryGetIndex(table.ObjectId, "IX.ID:ONE", out _).ShouldBeTrue();
        result.Catalog.Scripts.ShouldHaveSingleItem().Objects.ShouldBe(["T:\"dbo\".\"script.rows:one\"", "I:\"dbo\".\"script.rows:one\".\"ix.id:one\""]);
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Cold Apply and Verify refuse receipts for colliding quoted object names")]
    [InlineData(SqlProvisioningMode.Apply, "CREATE TABLE a.\"b.c\"(id INT)", "CREATE TABLE \"a.b\".c(id INT)", null)]
    [InlineData(SqlProvisioningMode.Verify, "CREATE TABLE a.\"b.c\"(id INT)", "CREATE TABLE \"a.b\".c(id INT)", null)]
    [InlineData(SqlProvisioningMode.Apply, "CREATE INDEX ix ON a.\"b.c\"(id)", "CREATE INDEX ix ON \"a.b\".c(id)", "CREATE TABLE \"a.b\".c(id INT)")]
    [InlineData(SqlProvisioningMode.Verify, "CREATE INDEX ix ON a.\"b.c\"(id)", "CREATE INDEX ix ON \"a.b\".c(id)", "CREATE TABLE \"a.b\".c(id INT)")]
    [InlineData(SqlProvisioningMode.Apply, "CREATE INDEX \"c.ix\" ON a.b(id)", "CREATE INDEX ix ON a.\"b.c\"(id)", "CREATE TABLE a.\"b.c\"(id INT)")]
    [InlineData(SqlProvisioningMode.Verify, "CREATE INDEX \"c.ix\" ON a.b(id)", "CREATE INDEX ix ON a.\"b.c\"(id)", "CREATE TABLE a.\"b.c\"(id INT)")]
    public async Task BuildAsync_QuotedObjectReceiptMismatch_ShouldRefuseColdBuild(SqlProvisioningMode mode, string wantedSql, string actualSql, string? setup)
    {
        // Arrange: insert a receipt for a different tuple under the declared body's checksum.
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(wantedSql)));
        var actual = new SqlDeclaredScript("001", actualSql, new SqlQueryParserOptions());
        string target = JsonEncodedText.Encode(actual.ObjectKey(0, (_, _) => SqlType.Integer)).ToString();
        var storage = new CrashCaptureSqlStorageStrategy();
        var seed = SqlDatabaseEngine.CreateBuilder("quoted-receipts");
        seed.Options.StorageStrategy = storage;
        await using (var engine = await seed.BuildAsync(CancellationToken.None))
        {
            await using var session = await SessionAsync(engine);
            await ExecuteAsync(session, "CREATE SCHEMA a");
            await ExecuteAsync(session, "CREATE SCHEMA \"a.b\"");
            if (setup is not null) { await ExecuteAsync(session, setup); }
            await ExecuteAsync(session, actualSql);
            var database = await engine.OpenDatabaseAsync("functions", CancellationToken.None);
            ulong generation = database.Catalog.DdlGeneration;
            string json = $$"""
                {"Name":"001","ContentHash":"{{hash}}","StatementCount":1,"CompletedStatements":1,"BaseGeneration":{{generation}},"Generation":{{generation}},"SchemaHash":null,"Objects":["{{target}}"],"StatementObjects":["{{target}}"]}
                """;
            var record = new DatabaseKeyWriter().AppendInt32(12).AppendString(json, Collation.Binary).ToArray();
            using var transaction = database.CatalogStorage.BeginTransaction();
            database.CatalogStorage.InsertRow(transaction, record);
            transaction.Commit();
        }
        var reopen = SqlDatabaseEngine.CreateBuilder("quoted-receipts");
        reopen.Options.StorageStrategy = storage.CaptureDurableImages();
        reopen.AddDatabase("functions", database =>
        {
            database.Provisioning = mode;
            database.Script("001", wantedSql);
        });

        // Act
        var failure = await Should.ThrowAsync<SqlSchemaMigrationException>(async () =>
        {
            await using var engine = await reopen.BuildAsync(CancellationToken.None);
        });

        // Assert
        failure.Message.ShouldContain("COHSQLS001");
        failure.Message.ShouldContain("statement 1");
    }
}
