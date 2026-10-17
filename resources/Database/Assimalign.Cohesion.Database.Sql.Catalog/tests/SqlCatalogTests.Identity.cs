using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Storage;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Catalog.Tests;

public partial class SqlCatalogTests
{
    [Fact(DisplayName = "Cohesion Test [Sql.Catalog] - Identity: reservations preserve table references and DDL generation")]
    public async Task AllocateIdentity_ReservationAndReopen_ShouldKeepMetadataAndSkipReservedBlock()
    {
        // Arrange
        var (catalog, harness) = OpenFresh();
        var table = await catalog.CreateTableAsync("dbo", "ids", [new SqlCatalogColumn("id", new DatabaseTypeInfo(DatabaseType.Int64), false,
            identity: new SqlCatalogIdentity(false, 100, 5))]);
        ulong generation = catalog.DdlGeneration;

        // Act
        catalog.AllocateIdentity(table).ShouldBe(100);
        catalog.AllocateIdentity(table).ShouldBe(105);
        var restored = Reopen(harness);

        // Assert
        catalog.TryGetTable("dbo", "ids", out var unchanged).ShouldBeTrue();
        unchanged.ShouldBeSameAs(table);
        catalog.DdlGeneration.ShouldBe(generation);
        restored.DdlGeneration.ShouldBe(generation);
        restored.TryGetTable("dbo", "ids", out var reopened).ShouldBeTrue();
        reopened.Columns.Single().Identity.ShouldNotBeNull().IsAlways.ShouldBeFalse();
        reopened.Columns.Single().Identity!.Start.ShouldBe(100);
        reopened.Columns.Single().Identity!.Increment.ShouldBe(5);
        restored.AllocateIdentity(reopened).ShouldBe(260);
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Catalog] - Identity: malformed persisted facets fail closed")]
    [InlineData(true, 1L)][InlineData(false, 0L)]
    public void Open_InvalidIdentityFacet_ShouldRefuse(bool nullable, long increment)
    {
        // Arrange
        using var storage = SqlStorage.Create(new MemoryStream(), new MemoryStream(), new MemoryStream(), "invalid-identity-facet");
        var writer = new DatabaseKeyWriter();
        writer.AppendInt32(1).AppendInt64(1).AppendString("dbo", Collation.Binary).AppendString("ids", Collation.Binary)
            .AppendInt32(1).AppendString("id", Collation.Binary).AppendInt8((sbyte)DatabaseType.Int64)
            .AppendInt32(-1).AppendInt32(-1).AppendInt32(-1).AppendBoolean(nullable).AppendNull().AppendInt32(0)
            .AppendInt8((sbyte)DatabaseObjectOwner.Adhoc).AppendNull().AppendInt32(5).AppendInt32(0)
            .AppendInt32(-1).AppendInt32(0).AppendNull().AppendInt32(0)
            .AppendBoolean(true).AppendBoolean(true).AppendInt64(1).AppendInt64(increment);
        using (var transaction = storage.BeginTransaction())
        {
            storage.InsertRow(transaction, writer.ToArray());
            transaction.Commit();
        }

        // Act / Assert
        Should.Throw<SqlCatalogException>(() => SqlCatalog.Open(storage)).Message.ShouldContain("id", Case.Sensitive);
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Catalog] - Identity: corrupt counter records fail closed")]
    [InlineData(false, false, 102L)][InlineData(true, false, 100L)][InlineData(false, true, 100L)]
    public async Task Open_InvalidIdentityCounter_ShouldRefuse(bool duplicate, bool orphan, long reserved)
    {
        // Arrange
        using var storage = SqlStorage.Create(new MemoryStream(), new MemoryStream(), new MemoryStream(), "invalid-identity");
        var catalog = SqlCatalog.Open(storage);
        var table = await catalog.CreateTableAsync("dbo", "ids", [new SqlCatalogColumn("id", new DatabaseTypeInfo(DatabaseType.Int64),
            identity: new SqlCatalogIdentity(true, 100, 5))]);
        var writer = new DatabaseKeyWriter();
        writer.AppendInt32(16).AppendInt64(orphan ? 1000 : (long)table.ObjectId).AppendInt64(reserved);
        using (var transaction = storage.BeginTransaction())
        {
            storage.InsertRow(transaction, writer.ToArray());
            if (duplicate) { storage.InsertRow(transaction, writer.ToArray()); }
            transaction.Commit();
        }

        // Act / Assert
        Should.Throw<SqlCatalogException>(() => SqlCatalog.Open(storage)).Message.ShouldContain("identity", Case.Insensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Catalog] - Identity: dropping its column removes the counter in the same commit")]
    public async Task DropColumnAsync_IdentityColumn_ShouldRemoveItsCounter()
    {
        // Arrange
        var (catalog, harness) = OpenFresh();
        var table = await catalog.CreateTableAsync("dbo", "ids", [Column("label", DatabaseType.Int32),
            new SqlCatalogColumn("id", new DatabaseTypeInfo(DatabaseType.Int32), false, identity: new SqlCatalogIdentity(true))]);
        catalog.AllocateIdentity(table).ShouldBe(1);

        // Act
        await catalog.DropColumnAsync("dbo", "ids", "id");
        var restored = Reopen(harness);

        // Assert
        restored.TryGetTable("dbo", "ids", out var reopened).ShouldBeTrue();
        reopened.Columns.ShouldHaveSingleItem().Name.ShouldBe("label");
        await restored.AddColumnAsync("dbo", "ids", new SqlCatalogColumn("id", new DatabaseTypeInfo(DatabaseType.Int32), false,
            identity: new SqlCatalogIdentity(true, 10, 2)));
        restored.TryGetTable("dbo", "ids", out var added).ShouldBeTrue();
        restored.AllocateIdentity(added).ShouldBe(10);
    }
}
