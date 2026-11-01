using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed class SqlSchemaFormatTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - Schema v7 temporal defaults require rebuilding (#1422)")]
    [InlineData("'12'")]
    [InlineData("'2.03:04:05.1234567'")]
    public void Deserialize_V7TemporalDefault_ShouldRefuseBeforeReinterpretingText(string literal)
    {
        // Arrange
        var schema = new SqlCompiledSchema(SqlCompiledSchema.CurrentFormat, "app", false, [],
            [new CompiledSchemaTable("items", "Items", [new("value", DatabaseType.TimeSpan, true, DefaultLiteral: literal)], null, [], [])], []);
        string document = schema.CanonicalDocument.Replace(SqlCompiledSchema.CurrentFormat, "cohesion/database-schema/v7", StringComparison.Ordinal);

        // Act
        var error = Should.Throw<SqlSchemaValidationException>(() => SqlCompiledSchemaSerializer.Deserialize(document));

        // Assert
        var detail = error.Errors.ShouldHaveSingleItem();
        detail.Code.ShouldBe(SqlSchemaValidationErrorCode.InvalidDocument);
        detail.Declaration.ShouldBe("schema.format");
        error.Message.ShouldContain("cohesion/database-schema/v11");
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Schema format: v4 SDK artifacts cannot enter Apply or Verify")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Provision_V4Artifact_ShouldRefuseBeforeCatalogMutation(bool verify)
    {
        // Arrange: the artifact's original SHA-256 is correct, but its format is obsolete.
        await using var engine = SqlDatabaseEngine.Create("formats", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        string document = V4Document(false);
        string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(document)));
        hash.Length.ShouldBe(64);
        ulong generation = database.Catalog.DdlGeneration;

        // Act
        var error = await Should.ThrowAsync<SqlSchemaValidationException>(async () =>
        {
            var artifact = SqlCompiledSchemaSerializer.Deserialize(document);
            if (verify) { await database.VerifySchemaAsync(artifact, CancellationToken.None); }
            else { await database.ApplySchemaAsync(artifact, CancellationToken.None); }
        });

        // Assert
        AssertFormatRefusal(error);
        database.Catalog.Tables.ShouldBeEmpty();
        database.Catalog.SchemaState.ShouldBeNull();
        database.Catalog.DdlGeneration.ShouldBe(generation);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Schema format: Apply and Verify refuse v4 recorded identities before comparing hashes")]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Provision_V4RecordedSchema_ShouldRefuseFormat(bool pending, bool verify)
    {
        // Arrange: the pre-normalization declaration applied NUMERIC(3) as scale zero,
        // but its v4 document retained scale:null. Hash those exact persisted bytes.
        await using var engine = SqlDatabaseEngine.Create("formats", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await SeedAsync(database, pending);
        var recorded = database.Catalog.SchemaState;
        var intent = database.Catalog.PendingMigration;
        var plan = database.SchemaProvisioner.LastPlan;
        ulong generation = database.Catalog.DdlGeneration;

        // Act
        var error = await Should.ThrowAsync<SqlSchemaValidationException>(async () =>
        {
            if (verify) { await database.VerifySchemaAsync(Current(pending), CancellationToken.None); }
            else { await database.ApplySchemaAsync(Current(pending), CancellationToken.None); }
        });

        // Assert: neither drift, invalid-hash recovery nor a zero-operation re-plan.
        AssertFormatRefusal(error);
        database.Catalog.SchemaState.ShouldBeSameAs(recorded);
        database.Catalog.PendingMigration.ShouldBeSameAs(intent);
        database.Catalog.DdlGeneration.ShouldBe(generation);
        database.SchemaProvisioner.LastPlan.ShouldBeSameAs(plan);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Schema format: Apply and Verify reopen refuse v4 markers and pending AddIndex targets")]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task BuildAsync_V4RecordedSchema_ShouldRefuseAtOpen(bool pending, bool verify)
    {
        // Arrange: capture the committed marker/intent as a crashed process leaves it.
        var storage = new CrashCaptureSqlStorageStrategy();
        await using (var engine = SqlDatabaseEngine.Create("formats", new SqlDatabaseEngineOptions { StorageStrategy = storage }))
        {
            var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
            await SeedAsync(database, pending);
            storage = storage.CaptureDurableImages();
        }
        var builder = SqlDatabaseEngine.CreateBuilder("formats");
        builder.Options.StorageStrategy = storage;
        builder.AddDatabase("app", declaration =>
        {
            declaration.Schema(schema => schema.Table<SchemaFormatRow>("items", table => table.Column(row => row.Value, "NUMERIC(3)")));
            declaration.Provisioning = verify ? SqlProvisioningMode.Verify : SqlProvisioningMode.Apply;
        });

        // Act
        var error = await Should.ThrowAsync<SqlSchemaValidationException>(async () =>
        {
            await using var unexpected = await builder.BuildAsync(CancellationToken.None);
        });

        // Assert
        AssertFormatRefusal(error);
    }

    private static async Task SeedAsync(SqlDatabase database, bool pending)
    {
        await database.ApplySchemaAsync(Current(false), CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO items VALUES (123)", cancellationToken: CancellationToken.None);
        string document = V4Document(pending);
        document.ShouldContain("\"precision\":3,\"scale\":null,\"customType\":\"NUMERIC(3)\"", Case.Sensitive);
        string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(document)));
        if (pending)
        {
            await database.Catalog.SavePendingMigrationAsync(new SqlCatalogPendingMigration(hash, document,
                [new SqlCatalogMigrationStep("AddIndex", "items", "ix_value", 1)], 1), CancellationToken.None);
        }
        else
        {
            await database.Catalog.SaveSchemaStateAsync(new SqlCatalogSchemaState(hash, document, database.Catalog.DdlGeneration), CancellationToken.None);
        }
        database.SchemaProvisioner.LastPlan.ShouldNotBeNull();
    }

    private static string V4Document(bool index) => Current(index).CanonicalDocument
        .Replace(SqlCompiledSchema.CurrentFormat, "cohesion/database-schema/v4", StringComparison.Ordinal)
        .Replace("\"scale\":0", "\"scale\":null", StringComparison.Ordinal);

    private static SqlCompiledSchema Current(bool index) => new(SqlCompiledSchema.CurrentFormat, "app", false, [],
        [new CompiledSchemaTable("items", "Tests.Row",
            [new CompiledSchemaColumn("value", DatabaseType.Decimal, true, Precision: 3, CustomType: "NUMERIC(3)")],
            null, index ? [new CompiledSchemaIndex("ix_value", ["value"])] : [], [])], []);

    private static void AssertFormatRefusal(SqlSchemaValidationException error)
    {
        var detail = error.Errors.ShouldHaveSingleItem();
        detail.Code.ShouldBe(SqlSchemaValidationErrorCode.InvalidDocument);
        detail.Declaration.ShouldBe("schema.format");
        error.Message.ShouldContain("cohesion/database-schema/v4", Case.Sensitive);
        error.Message.ShouldContain("cohesion/database-schema/v11", Case.Sensitive);
        error.Message.ShouldContain("Rebuild", Case.Sensitive);
        error.Message.ShouldContain("recreate", Case.Sensitive);
    }
}
