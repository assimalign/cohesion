using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlDefinedFunctionTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - Declared namespace scripts retain receipts and survive cold Apply and Verify")]
    [InlineData(SqlProvisioningMode.Apply)]
    [InlineData(SqlProvisioningMode.Verify)]
    public async Task BuildAsync_NamespaceScript_ShouldValidateCreatedObjectsAndAbsenceReceipt(SqlProvisioningMode mode)
    {
        // Arrange
        const string sql = "CREATE SCHEMA sales; CREATE TABLE sales.items(id INT); INSERT INTO sales.items VALUES (7); " +
            "CREATE SCHEMA transient; DROP SCHEMA transient; DROP SCHEMA IF EXISTS absent CASCADE;";
        var storage = new CrashCaptureSqlStorageStrategy();
        await using (var engine = await ScriptBuilder(storage, sql: sql).BuildAsync(CancellationToken.None))
        {
            var database = await engine.OpenDatabaseAsync("functions", CancellationToken.None);
            var receipt = database.Catalog.Scripts.ShouldHaveSingleItem();
            receipt.CompletedStatements.ShouldBe(6);
            receipt.Objects.ShouldBe(["S:\"sales\"", "T:\"sales\".\"items\"", "S:\"transient\""]);
            receipt.StatementObjects.Last().ShouldBe("S:\"absent\"");
            var schema = database.Catalog.Namespaces.Single(schema => schema.Name == "sales");
            schema.Owner.ShouldBe(DatabaseObjectOwner.Schema);
            schema.OwningSchema.ShouldBe("functions");
            // Force cold receipt validation without invalidating the independently verified
            // typed-schema generation, which Verify deliberately refuses after unrelated DDL.
            await database.Catalog.SaveScriptStateAsync(new SqlCatalogScriptState(new string('0', 64), database.Catalog.DdlGeneration), CancellationToken.None);
        }

        // Act
        await using var reopened = await ScriptBuilder(storage.CaptureDurableImages(), sql: sql, mode: mode).BuildAsync(CancellationToken.None);
        var result = await reopened.OpenDatabaseAsync("functions", CancellationToken.None);
        await using var reader = await result.CreateSessionAsync(CancellationToken.None);

        // Assert
        (await RowsAsync(reader, "SELECT id FROM sales.items")).ShouldBe([[7]]);
        result.Catalog.Scripts.ShouldHaveSingleItem().CompletedStatements.ShouldBe(6);
        result.Catalog.Namespaces.Select(schema => schema.Name).Order().ShouldBe(["dbo", "sales"]);
        result.Catalog.TryGetTable("dbo", "typed_rows", out _).ShouldBeTrue();
        await Should.ThrowAsync<DatabaseObjectLockedException>(() => ExecuteAsync(reader, "DROP SCHEMA sales CASCADE"));
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Declared DROP SCHEMA CASCADE over typed tables is refused before opening storage")]
    public async Task BuildAsync_NamespaceCascadeOverTypedObjects_ShouldFailAdmission()
    {
        // Arrange
        var storage = new CrashCaptureSqlStorageStrategy();
        var builder = ScriptBuilder(storage, sql: "DROP SCHEMA dbo CASCADE");

        // Act
        var failure = await Should.ThrowAsync<SqlSchemaMigrationException>(async () => await builder.BuildAsync(CancellationToken.None));

        // Assert
        failure.Message.ShouldContain("CS03205");
        failure.Message.ShouldContain("Nothing was opened or written");
    }
}
