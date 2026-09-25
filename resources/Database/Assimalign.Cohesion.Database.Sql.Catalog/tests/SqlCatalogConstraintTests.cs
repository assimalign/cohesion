using System;
using System.IO;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Indexing;
using Assimalign.Cohesion.Database.Sql.Storage;
using Assimalign.Cohesion.Database.Types;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Catalog.Tests;

public sealed class SqlCatalogConstraintTests
{
    [Fact]
    public async Task Constraints_AfterUncheckpointedReopen_ShouldRetainDefinitionsAndAlterations()
    {
        using var data = new MemoryStream();
        using var journal = new MemoryStream();
        using var storage = SqlStorage.Create(new NonClosingStream(data), new NonClosingStream(journal), new MemoryStream(), "constraints");
        ISqlCatalog catalog = SqlCatalog.Open(storage);
        var reference = new SqlCatalogConstraint("fk_parent", SqlCatalogConstraintKind.Reference,
            ["parent_id"], "dbo", "parent", ["id"], onDelete: SqlCatalogReferentialAction.Cascade);
        var check = new SqlCatalogConstraint("ck_positive", SqlCatalogConstraintKind.Check,
            ["qty"], checkExpression: "qty > 0");
        await SqlCatalog.CreateTableAsync(catalog, "dbo", "child",
            [Column("id"), Column("parent_id"), Column("qty")], ["id"], [reference],
            DatabaseObjectOwner.Schema, "app", default);
        await SqlCatalog.AddConstraintAsync(catalog, "dbo", "child", check, default);
        await catalog.AddColumnAsync("dbo", "child", Column("extra"));

        // Copy the live no-force data and WAL, without checkpoint or clean shutdown.
        using var recoveredStorage = SqlStorage.Open(Copy(data), Copy(journal), new MemoryStream());
        ISqlCatalog recovered = SqlCatalog.Open(recoveredStorage);
        recovered.TryGetTable("dbo", "child", out SqlCatalogTable table).ShouldBeTrue();
        table.Owner.ShouldBe(DatabaseObjectOwner.Schema);
        table.OwningSchema.ShouldBe("app");
        table.Constraints.Count.ShouldBe(2);
        table.Constraints[0].Name.ShouldBe("fk_parent");
        table.Constraints[0].ReferencedSchema.ShouldBe("dbo");
        table.Constraints[0].ReferencedTable.ShouldBe("parent");
        table.Constraints[0].ReferencedColumns.ShouldBe(["id"]);
        table.Constraints[0].OnDelete.ShouldBe(SqlCatalogReferentialAction.Cascade);
        table.Constraints[1].CheckExpression.ShouldBe("qty > 0");
        table.FindColumn("extra").ShouldNotBeNull();

        await SqlCatalog.DropConstraintAsync(recovered, "dbo", "child", "ck_positive", default);
        SqlCatalog.Open(recoveredStorage).Tables.ShouldHaveSingleItem().Constraints.ShouldHaveSingleItem().Name.ShouldBe("fk_parent");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OlderTableRecords_ShouldLoadWithoutConstraints(bool ownershipSuffix)
    {
        using var storage = SqlStorage.Create(new MemoryStream(), new MemoryStream(), new MemoryStream(), "legacy");
        var writer = LegacyTable();
        if (ownershipSuffix)
        {
            writer.AppendInt8((sbyte)DatabaseObjectOwner.Schema).AppendString("app", Collation.Binary);
        }
        using (var transaction = storage.BeginTransaction())
        {
            storage.InsertRow(transaction, writer.ToArray());
            transaction.Commit();
        }
        SqlCatalogTable table = SqlCatalog.Open(storage).Tables.ShouldHaveSingleItem();
        table.Constraints.ShouldBeEmpty();
        table.Owner.ShouldBe(ownershipSuffix ? DatabaseObjectOwner.Schema : DatabaseObjectOwner.Adhoc);
    }

    [Fact]
    public void UnknownConstraintRecordVersion_ShouldFailClosed()
    {
        using var storage = SqlStorage.Create(new MemoryStream(), new MemoryStream(), new MemoryStream(), "future");
        var writer = LegacyTable();
        writer.AppendInt8((sbyte)DatabaseObjectOwner.Adhoc).AppendNull().AppendInt32(99).AppendInt32(0);
        using (var transaction = storage.BeginTransaction())
        {
            storage.InsertRow(transaction, writer.ToArray());
            transaction.Commit();
        }
        Should.Throw<SqlCatalogException>(() => SqlCatalog.Open(storage)).Message.ShouldContain("version 99");
    }

    [Fact]
    public async Task ReservedTable_ShouldStayInvisibleUntilIndexesAndConstraintsArePublishedTogether()
    {
        using var data = new MemoryStream();
        using var journal = new MemoryStream();
        using var storage = SqlStorage.Create(new NonClosingStream(data), new NonClosingStream(journal), new MemoryStream(), "publish");
        ISqlCatalog catalog = SqlCatalog.Open(storage);
        var check = new SqlCatalogConstraint("ck_id", SqlCatalogConstraintKind.Check, ["id"], checkExpression: "id > 0");
        SqlCatalogTable table = await SqlCatalog.ReserveTableAsync(catalog, "dbo", "t", [Column("id")], [], [check],
            DatabaseObjectOwner.Adhoc, null, default);
        catalog.Tables.ShouldBeEmpty();
        using (var reservedStorage = SqlStorage.Open(Copy(data), Copy(journal), new MemoryStream()))
        {
            ISqlCatalog reservedCatalog = SqlCatalog.Open(reservedStorage);
            reservedCatalog.Tables.ShouldBeEmpty();
            (await reservedCatalog.CreateTableAsync("dbo", "next", [Column("id")])).ObjectId.ShouldBeGreaterThan(table.ObjectId);
        }

        var index = new SqlCatalogIndex(table.ObjectId, "uq_id", ["id"], true);
        BTreeIndexRegistration[] registrations = [new(table.ObjectId, new IndexDefinition("uq_id", IndexKind.BTree, true), 7)];
        await SqlCatalog.PublishTableAsync(catalog, table, [index], registrations, cancellationToken: default);

        using var reopenedStorage = SqlStorage.Open(Copy(data), Copy(journal), new MemoryStream());
        ISqlCatalog reopened = SqlCatalog.Open(reopenedStorage);
        reopened.Tables.ShouldHaveSingleItem().Constraints.ShouldHaveSingleItem().Name.ShouldBe("ck_id");
        reopened.GetIndexes(table.ObjectId).ShouldHaveSingleItem().IsUnique.ShouldBeTrue();
        reopened.GetIndexRegistrations().ShouldHaveSingleItem().Definition.IsUnique.ShouldBeTrue();
    }

    [Fact]
    public async Task PrimaryKeyIndexMarker_ShouldPersistWithoutClassifyingEquivalentUniqueIndexesAsPrimaryKeys()
    {
        using var data = new MemoryStream();
        using var journal = new MemoryStream();
        using var storage = SqlStorage.Create(new NonClosingStream(data), new NonClosingStream(journal), new MemoryStream(), "pk-index");
        ISqlCatalog catalog = SqlCatalog.Open(storage);
        SqlCatalogTable table = await SqlCatalog.ReserveTableAsync(catalog, "dbo", "t", [Column("id")], ["id"], [],
            DatabaseObjectOwner.Adhoc, null, default);
        SqlCatalogIndex[] indexes = [new(table.ObjectId, "pk_t", ["id"], true, isPrimaryKey: true), new(table.ObjectId, "uq_id", ["id"], true)];
        BTreeIndexRegistration[] registrations = [new(table.ObjectId, new IndexDefinition("pk_t", IndexKind.BTree, true), 7), new(table.ObjectId, new IndexDefinition("uq_id", IndexKind.BTree, true), 8)];
        await SqlCatalog.PublishTableAsync(catalog, table, indexes, registrations, cancellationToken: default);

        using var reopenedStorage = SqlStorage.Open(Copy(data), Copy(journal), new MemoryStream());
        ISqlCatalog reopened = SqlCatalog.Open(reopenedStorage);
        reopened.TryGetIndex(table.ObjectId, "pk_t", out SqlCatalogIndex primary).ShouldBeTrue();
        reopened.TryGetIndex(table.ObjectId, "uq_id", out SqlCatalogIndex unique).ShouldBeTrue();
        primary.IsPrimaryKey.ShouldBeTrue();
        unique.IsPrimaryKey.ShouldBeFalse();
    }

    [Fact]
    public void ConstraintDefinition_ShouldSnapshotMutableInputs()
    {
        string[] columns = ["parent_id"];
        var constraint = new SqlCatalogConstraint("fk", SqlCatalogConstraintKind.Reference, columns, "dbo", "parent", ["id"]);
        SqlCatalogConstraint[] definitions = [constraint];
        var table = new SqlCatalogTable(1, "dbo", "child", [Column("parent_id")], constraints: definitions);
        columns[0] = "changed";
        definitions[0] = new SqlCatalogConstraint("ck", SqlCatalogConstraintKind.Check, [], checkExpression: "1 = 1");
        table.Constraints.ShouldHaveSingleItem().Columns.ShouldBe(["parent_id"]);
    }

    private static SqlCatalogColumn Column(string name) => new(name, new DatabaseTypeInfo(DatabaseType.Int32));

    private static MemoryStream Copy(MemoryStream source)
    {
        var copy = new MemoryStream();
        copy.Write(source.ToArray());
        copy.Position = 0;
        return copy;
    }

    private static DatabaseKeyWriter LegacyTable()
    {
        var writer = new DatabaseKeyWriter();
        writer.AppendInt32(1).AppendInt64(1).AppendString("dbo", Collation.Binary)
            .AppendString("legacy", Collation.Binary).AppendInt32(1)
            .AppendString("id", Collation.Binary).AppendInt8((sbyte)DatabaseType.Int32)
            .AppendInt32(-1).AppendInt32(-1).AppendInt32(-1).AppendBoolean(true)
            .AppendNull().AppendInt32(0);
        return writer;
    }
}
