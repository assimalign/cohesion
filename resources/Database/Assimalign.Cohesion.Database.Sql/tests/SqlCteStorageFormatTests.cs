using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed class SqlCteStorageFormatTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - WITH format: format-15 CHECK text would drift under the new identifier vocabulary")]
    [InlineData("search")]
    [InlineData("materialized")]
    public async Task VerifySchema_FormatFifteenCheckText_ShouldDemonstrateCanonicalDrift(string name)
    {
        // Arrange: format 15 rendered these ordinary identifiers without quotes. The
        // SQL-independent catalog API reproduces those persisted bytes without a legacy reader.
        string oldText = name + " >= 0";
        string constraintName = "ck_" + name;
        var schema = new SqlCompiledSchema(SqlCompiledSchema.CurrentFormat, "app", false, [],
            [new CompiledSchemaTable("items", "Items", [new(name, DatabaseType.Int32, true)], null, [],
                [new(constraintName, CompiledSchemaConstraintKind.Check, [name], null, [], new(oldText))])], []);
        var storage = new CrashCaptureSqlStorageStrategy();
        await using (var engine = SqlDatabaseEngine.Create("cte-format", new() { StorageStrategy = storage }))
        {
            var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
            await database.ApplySchemaAsync(schema, CancellationToken.None);
            database.Catalog.TryGetTable("dbo", "items", out var table).ShouldBeTrue();
            table.Constraints[0].CheckExpression.ShouldBe($"\"{name}\" >= 0");
            var legacy = new SqlCatalogTable(table.ObjectId, table.Schema, table.Name, table.Columns,
                table.PrimaryKeyColumns, table.Owner, table.OwningSchema,
                [new(constraintName, SqlCatalogConstraintKind.Check, [name], checkExpression: oldText)],
                table.DroppedColumnOrdinals);
            await database.Catalog.PublishTableAsync(legacy, [], [], replaceExisting: true, cancellationToken: CancellationToken.None);
            await database.Catalog.SetRecordSpaceFormatVersionAsync(15, CancellationToken.None);

            // Act: an already-open catalog can demonstrate the Verify failure that
            // would follow reopening format 15 if the format gate were removed.
            var drift = await Should.ThrowAsync<SqlSchemaMigrationException>(async () =>
                await database.VerifySchemaAsync(schema, CancellationToken.None));

            // Assert: the CHECK text, rather than its meaning or a record layout, differs.
            drift.Message.ShouldContain($"constraint '{constraintName}' differs from its declaration", Case.Sensitive);
            SqlPersistedExpression.AreEquivalent(SqlPersistedExpression.Load(oldText, "test"),
                SqlPersistedExpression.Load($"\"{name}\" >= 0", "test")).ShouldBeTrue();
            storage = storage.CaptureDurableImages();
        }

        // Act / Assert: actual reopening refuses those format-15 bytes before data access.
        await using var reopening = SqlDatabaseEngine.Create("cte-format", new() { StorageStrategy = storage });
        var refusal = await Should.ThrowAsync<SqlDataStorageFormatException>(async () =>
            await reopening.OpenDatabaseAsync("app", CancellationToken.None));
        refusal.Message.ShouldContain("uses data-storage format 15", Case.Sensitive);
        refusal.Message.ShouldContain("supports only format 17", Case.Sensitive);
    }
}
