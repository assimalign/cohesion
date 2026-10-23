using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Indexing;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Catalog.Tests;

public partial class SqlCatalogTests
{
    [Fact(DisplayName = "Cohesion Test [Sql.Catalog] - ALTER: insert default and frozen missing literal persist independently")]
    public async Task PublishTable_DefaultSplit_ShouldRoundTripBothFacets()
    {
        // Arrange
        var (catalog, harness) = OpenFresh();
        var table = await catalog.CreateTableAsync("dbo", "t", [Column("id", DatabaseType.Int32)], cancellationToken: CancellationToken.None);
        table = await catalog.AddColumnAsync("dbo", "t", new SqlCatalogColumn("v", new DatabaseTypeInfo(DatabaseType.Int32),
            defaultLiteral: "9", missingValueLiteral: "7"), CancellationToken.None);
        var columns = table.Columns.Select(column => column.Name == "v"
            ? new SqlCatalogColumn("v", column.Type, defaultLiteral: null, missingValueLiteral: column.MissingValueLiteral) : column).ToArray();

        // Act
        await catalog.PublishTableAsync(new SqlCatalogTable(table.ObjectId, table.Schema, table.Name, columns), [], [], true, CancellationToken.None);

        // Assert
        var restored = harness.Reopen();
        restored.TryGetTable("dbo", "t", out var result).ShouldBeTrue();
        result.FindColumn("v")!.DefaultLiteral.ShouldBeNull();
        result.FindColumn("v")!.MissingValueLiteral.ShouldBe("7");
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Catalog] - RENAME: identity indexes FKs and dropped ordinals publish as one definition")]
    public async Task Rename_ParentAndColumn_ShouldKeepPhysicalIdentityAndDependents()
    {
        // Arrange
        var (catalog, harness) = OpenFresh();
        var table = await catalog.CreateTableAsync("dbo", "p", [Column("id", DatabaseType.Int32, false), Column("dropped", DatabaseType.String)], ["id"], CancellationToken.None);
        table = await catalog.DropColumnAsync("dbo", "p", "dropped", CancellationToken.None);
        var child = await catalog.CreateTableAsync("dbo", "c", [Column("pid", DatabaseType.Int32)], null,
            DatabaseObjectOwner.Adhoc, null, CancellationToken.None,
            [new SqlCatalogConstraint("fk", SqlCatalogConstraintKind.Reference, ["pid"], "dbo", "p", ["id"])]);
        await catalog.CreateIndexAsync(new SqlCatalogIndex(table.ObjectId, "ix_id", ["id"], true),
            [new BTreeIndexRegistration(table.ObjectId, new IndexDefinition("ix_id", IndexKind.BTree, true), 7)], CancellationToken.None);
        ulong generation = catalog.DdlGeneration;

        // Act
        table = await catalog.RenameTableAsync(table, "renamed", new Dictionary<string, string>(), CancellationToken.None);
        table = await catalog.RenameColumnAsync(table, "id", "key_id", new Dictionary<string, string>(), CancellationToken.None);

        // Assert
        catalog.DdlGeneration.ShouldBe(generation + 2);
        var restored = harness.Reopen();
        restored.TryGetTable("dbo", "p", out _).ShouldBeFalse();
        restored.TryGetTable("dbo", "renamed", out var parent).ShouldBeTrue();
        parent.ObjectId.ShouldBe(table.ObjectId);
        parent.DroppedColumnOrdinals.ShouldBe(new[] { 1 });
        parent.PrimaryKeyColumns.ShouldBe(new[] { "key_id" });
        restored.GetIndexes(parent.ObjectId).Single().ColumnNames.ShouldBe(new[] { "key_id" });
        restored.TryGetTable("dbo", "c", out var result).ShouldBeTrue();
        result.ObjectId.ShouldBe(child.ObjectId);
        result.Constraints.Single().ReferencedTable.ShouldBe("renamed");
        result.Constraints.Single().ReferencedColumns.ShouldBe(new[] { "key_id" });
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Catalog] - RENAME: a stale definition cannot replace committed metadata")]
    public async Task Rename_StaleDefinition_ShouldRefuseWithoutAdvancingGeneration()
    {
        // Arrange
        var (catalog, harness) = OpenFresh();
        var original = await catalog.CreateTableAsync("dbo", "t", [Column("id", DatabaseType.Int32)], cancellationToken: CancellationToken.None);
        await catalog.AddColumnAsync("dbo", "t", Column("v", DatabaseType.Int32), CancellationToken.None);
        ulong generation = catalog.DdlGeneration;

        // Act / Assert
        var failure = await Should.ThrowAsync<SqlCatalogException>(async () => await catalog.RenameTableAsync(original, "u", new Dictionary<string, string>(), CancellationToken.None));
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS04064");
        catalog.DdlGeneration.ShouldBe(generation);
        harness.Reopen().TryGetTable("dbo", "t", out var table).ShouldBeTrue();
        table.Columns.Count.ShouldBe(2);
    }
}
