using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed class SqlDecimalMetadataTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - Raw decimal metadata: omitted scale applies, skips, verifies and reopens")]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(28)]
    public async Task ApplySchemaAsync_OmittedDecimalScale_ShouldConvergeAcrossReopen(int precision)
    {
        // Arrange
        var schema = Declare(precision, null);
        var storage = new CrashCaptureSqlStorageStrategy();
        await using (var engine = SqlDatabaseEngine.Create("decimal", new SqlDatabaseEngineOptions { StorageStrategy = storage }))
        {
            var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);

            // Act
            var applied = await database.ApplySchemaAsync(schema, CancellationToken.None);

            // Assert
            applied.OperationCount.ShouldBe(1);
            applied.WasAlreadyApplied.ShouldBeFalse();
            database.Catalog.TryGetTable("dbo", "items", out var table).ShouldBeTrue();
            table.Columns[0].Type.Precision.ShouldBe(precision);
            table.Columns[0].Type.Scale.ShouldBe(0);
            (await database.ApplySchemaAsync(schema, CancellationToken.None)).WasAlreadyApplied.ShouldBeTrue();
            (await database.SchemaProvisioner.VerifyAsync(schema, CancellationToken.None)).WasAlreadyApplied.ShouldBeTrue();
            storage = storage.CaptureDurableImages();
        }

        // Act / Assert: the recorded document and metadata survive a new engine.
        await using var reopenedEngine = SqlDatabaseEngine.Create("reopened", new SqlDatabaseEngineOptions { StorageStrategy = storage });
        var reopened = await reopenedEngine.OpenDatabaseAsync("app", CancellationToken.None);
        (await reopened.ApplySchemaAsync(schema, CancellationToken.None)).WasAlreadyApplied.ShouldBeTrue();
        (await reopened.SchemaProvisioner.VerifyAsync(schema, CancellationToken.None)).WasAlreadyApplied.ShouldBeTrue();
        reopened.Catalog.SchemaState.ShouldNotBeNull().CanonicalDocument.ShouldBe(schema.CanonicalDocument);
        await using var session = await reopened.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO items VALUES (1.4)", cancellationToken: CancellationToken.None);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Verify: decimal drift reports the raw null and zero scale values")]
    public async Task VerifyAsync_RawScaleMismatch_ShouldShowComparedFields()
    {
        // Arrange: inject catalog drift without SQL's normalization.
        await using var engine = SqlDatabaseEngine.Create("decimal", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        var table = await database.Catalog.ReserveTableAsync("dbo", "items",
            [new SqlCatalogColumn("value", new DatabaseTypeInfo(DatabaseType.Decimal, precision: 3))],
            primaryKeyColumns: null, constraints: [], owner: DatabaseObjectOwner.Schema, owningSchema: "app",
            cancellationToken: CancellationToken.None);
        await database.Catalog.PublishTableAsync(table, [], [], cancellationToken: CancellationToken.None);

        // Act
        var error = await Should.ThrowAsync<SqlSchemaMigrationException>(async () =>
            await database.SchemaProvisioner.VerifyAsync(Declare(3, 0), CancellationToken.None));

        // Assert
        error.Message.ShouldContain("column 'value'", Case.Sensitive);
        error.Message.ShouldContain("Decimal(MaxLength=null, Precision=3, Scale=null)", Case.Sensitive);
        error.Message.ShouldContain("Decimal(MaxLength=null, Precision=3, Scale=0)", Case.Sensitive);
    }

    private static SqlCompiledSchema Declare(int precision, int? scale)
        => new(SqlCompiledSchema.CurrentFormat, "app", false, [],
            [new CompiledSchemaTable("items", "Row",
                [new CompiledSchemaColumn("value", DatabaseType.Decimal, true, Precision: precision, Scale: scale)],
                null, [], [])], []);
}
