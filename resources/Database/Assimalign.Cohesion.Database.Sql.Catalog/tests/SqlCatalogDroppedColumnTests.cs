using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Indexing;
using Assimalign.Cohesion.Database.Sql.Storage;
using Assimalign.Cohesion.Database.Types;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Catalog.Tests;

/// <summary>
/// The catalog's physical column layout (#1241): DROP COLUMN marks the column's physical
/// ordinal dropped instead of renumbering the columns behind it, ADD COLUMN appends a new
/// physical ordinal, every alteration and reopen keeps the layout, and a published
/// replacement may not renumber it.
/// </summary>
public sealed class SqlCatalogDroppedColumnTests
{
    private static SqlCatalogColumn Column(string name, DatabaseType type = DatabaseType.Int32, bool nullable = true)
        => new(name, new DatabaseTypeInfo(type), nullable);

    [Fact(DisplayName = "Cohesion Test [Sql.Catalog] - Dropped columns: DROP marks the physical ordinal, ADD appends one, and the layout survives every alteration and reopen")]
    public async Task DropAndAddColumns_ShouldKeepPhysicalOrdinalsAcrossAlterationsAndReopen()
    {
        // Arrange
        using var data = new MemoryStream();
        using var journal = new MemoryStream();
        using var storage = SqlStorage.Create(new NonClosingStream(data), new NonClosingStream(journal), new MemoryStream(), "dropped");
        ISqlCatalog catalog = SqlCatalog.Open(storage);
        await catalog.CreateTableAsync("dbo", "t", [Column("id", nullable: false), Column("a"), Column("b", DatabaseType.String), Column("c")], ["id"]);

        // Act: drop a middle column, re-add its name, drop another, then change constraints.
        var afterDrop = await catalog.DropColumnAsync("dbo", "t", "B");
        var afterReAdd = await catalog.AddColumnAsync("dbo", "t", Column("b", DatabaseType.Int64));
        await catalog.DropColumnAsync("dbo", "t", "a");
        var check = new SqlCatalogConstraint("ck_c", SqlCatalogConstraintKind.Check, [], checkExpression: "c > 0");
        await SqlCatalog.AddConstraintAsync(catalog, "dbo", "t", check, default);
        await SqlCatalog.DropConstraintAsync(catalog, "dbo", "t", "ck_c");
        await SqlCatalog.AddConstraintAsync(catalog, "dbo", "t", check, default);

        // Assert: each step's layout.
        afterDrop.Columns.Select(column => column.Name).ShouldBe(["id", "a", "c"]);
        afterDrop.DroppedColumnOrdinals.ShouldBe([2]);
        afterDrop.PhysicalColumnCount.ShouldBe(4);
        Enumerable.Range(0, 3).Select(afterDrop.GetPhysicalOrdinal).ShouldBe([0, 1, 3]);
        afterReAdd.Columns.Select(column => column.Name).ShouldBe(["id", "a", "c", "b"]);
        afterReAdd.PhysicalColumnCount.ShouldBe(5);
        afterReAdd.GetPhysicalOrdinal(3).ShouldBe(4);

        catalog.TryGetTable("dbo", "t", out var live).ShouldBeTrue();
        AssertFinalLayout(live);
        live.Constraints.ShouldHaveSingleItem().Name.ShouldBe("ck_c");

        // An index on the re-added column binds by name.
        await catalog.CreateIndexAsync(new SqlCatalogIndex(live.ObjectId, "ix_b", ["b"], false),
            [new BTreeIndexRegistration(live.ObjectId, new IndexDefinition("ix_b", IndexKind.BTree, false), 9)]);

        // ...and after a reopen without a checkpoint.
        using var reopenedStorage = SqlStorage.Open(Copy(data), Copy(journal), new MemoryStream());
        ISqlCatalog reopened = SqlCatalog.Open(reopenedStorage);
        reopened.TryGetTable("dbo", "t", out var persisted).ShouldBeTrue();
        AssertFinalLayout(persisted);
        persisted.FindColumn("b").ShouldNotBeNull().Type.Type.ShouldBe(DatabaseType.Int64);
        reopened.TryGetIndex(persisted.ObjectId, "ix_b", out _).ShouldBeTrue();

        static void AssertFinalLayout(SqlCatalogTable table)
        {
            table.Columns.Select(column => column.Name).ShouldBe(["id", "c", "b"]);
            table.DroppedColumnOrdinals.ShouldBe([1, 2]);
            table.PhysicalColumnCount.ShouldBe(5);
            Enumerable.Range(0, 3).Select(table.GetPhysicalOrdinal).ShouldBe([0, 3, 4]);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Catalog] - Dropped columns: a table record written before the layout extension loads with no dropped column")]
    public void TableRecord_ExtensionVersionTwo_ShouldLoadWithoutDroppedColumns()
    {
        // Arrange: a table record as data-storage format 5 wrote it (extension version 2),
        // so the engine's format gate, not a decode error, refuses such a database.
        using var storage = SqlStorage.Create(new MemoryStream(), new MemoryStream(), new MemoryStream(), "format-five");
        var record = TableRecordPrefix(1, "dbo", "legacy", ("id", false), ("note", true))
            .AppendInt32(2).AppendInt32(0)          // extension version 2, no constraints
            .AppendInt32(-1).AppendInt32(-1);       // no collation overrides
        Insert(storage, record.ToArray());

        // Act
        ISqlCatalog catalog = SqlCatalog.Open(storage);

        // Assert
        catalog.TryGetTable("dbo", "legacy", out var table).ShouldBeTrue();
        table.Columns.Select(column => column.Name).ShouldBe(["id", "note"]);
        table.DroppedColumnOrdinals.ShouldBeEmpty();
        table.PhysicalColumnCount.ShouldBe(2);
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Catalog] - Dropped columns: a persisted layout that cannot describe the table is refused at load")]
    [InlineData(new[] { -1 }, "The persisted dropped-column count -1")]
    [InlineData(new[] { 1, 3 }, "outside its 3 physical columns")]
    [InlineData(new[] { 2, 0, 0 }, "listed more than once")]
    [InlineData(new[] { 1, -2 }, "outside its 3 physical columns")]
    public void TableRecord_InvalidDroppedOrdinals_ShouldBeRefusedAtLoad(int[] persisted, string message)
    {
        // Arrange: the dropped count, then the ordinals, after two live columns.
        using var storage = SqlStorage.Create(new MemoryStream(), new MemoryStream(), new MemoryStream(), "damaged");
        var record = TableRecordPrefix(1, "dbo", "damaged", ("id", false), ("note", true))
            .AppendInt32(3).AppendInt32(0).AppendInt32(-1).AppendInt32(-1);
        foreach (int value in persisted)
        {
            record.AppendInt32(value);
        }

        Insert(storage, record.ToArray());

        // Act / Assert
        Should.Throw<SqlCatalogException>(() => SqlCatalog.Open(storage)).Message.ShouldContain(message);
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Catalog] - Dropped columns: a published replacement that renumbers the layout is refused, one that appends is kept")]
    public async Task PublishTable_Replacement_ShouldKeepThePhysicalLayout()
    {
        // Arrange: (id, a, b, c) with b dropped.
        using var storage = SqlStorage.Create(new MemoryStream(), new MemoryStream(), new MemoryStream(), "publish-layout");
        ISqlCatalog catalog = SqlCatalog.Open(storage);
        await catalog.CreateTableAsync("dbo", "t", [Column("id", nullable: false), Column("a"), Column("b"), Column("c")], ["id"]);
        var current = await catalog.DropColumnAsync("dbo", "t", "b");
        SqlCatalogTable Replacement(SqlCatalogColumn[] columns, int[]? dropped)
            => new(current.ObjectId, "dbo", "t", columns, ["id"], droppedColumnOrdinals: dropped);

        // Act / Assert: forgetting the dropped ordinal would read c from b's component, and
        // reordering or removing a live column would read it from another column's.
        foreach (var renumbered in new[]
        {
            Replacement([.. current.Columns], null),
            Replacement([.. current.Columns], [1]),
            Replacement([current.Columns[0], current.Columns[2], current.Columns[1]], [2]),
            Replacement([current.Columns[0], current.Columns[1]], [2]),
        })
        {
            (await Should.ThrowAsync<SqlCatalogException>(async () =>
                await SqlCatalog.PublishTableAsync(catalog, renumbered, [], [], replaceExisting: true)))
                .Message.ShouldContain("does not keep the physical column layout");
            catalog.TryGetTable("dbo", "t", out var unchanged).ShouldBeTrue();
            unchanged.ShouldBeSameAs(current);
        }

        var appended = Replacement([.. current.Columns, Column("d")], [2]);
        await SqlCatalog.PublishTableAsync(catalog, appended, [], [], replaceExisting: true);
        catalog.TryGetTable("dbo", "t", out var published).ShouldBeTrue();
        published.GetPhysicalOrdinal(3).ShouldBe(4);

        // A new table starts with no dropped column.
        var reserved = await SqlCatalog.ReserveTableAsync(catalog, "dbo", "fresh", [Column("id"), Column("x")], null, [],
            DatabaseObjectOwner.Adhoc, null, default);
        var withDropped = new SqlCatalogTable(reserved.ObjectId, "dbo", "fresh", reserved.Columns, droppedColumnOrdinals: [0]);
        (await Should.ThrowAsync<SqlCatalogException>(async () =>
            await SqlCatalog.PublishTableAsync(catalog, withDropped, [], [])))
            .Message.ShouldContain("cannot be created with dropped columns");
        catalog.TryGetTable("dbo", "fresh", out _).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Catalog] - Dropped columns: a definition outgrown by its dropped ordinals is refused with the cause and the remedy, the table intact")]
    public async Task AddDropColumn_DroppedOrdinalsOutgrowTheRecord_ShouldRefuseWithTheRemedy()
    {
        // Arrange: each ADD/DROP cycle leaves one dropped ordinal, an integer component in the
        // table record, which is never reclaimed.
        using var data = new MemoryStream();
        using var journal = new MemoryStream();
        using var storage = SqlStorage.Create(new NonClosingStream(data), new NonClosingStream(journal), new MemoryStream(), "outgrown");
        ISqlCatalog catalog = SqlCatalog.Open(storage);
        await catalog.CreateTableAsync("dbo", "t", [Column("id", nullable: false), Column("note", DatabaseType.String)], ["id"]);
        SqlCatalogException? failure = null;
        int cycles = 0;

        // Act
        while (failure is null && cycles < 5_000)
        {
            try
            {
                await catalog.AddColumnAsync("dbo", "t", Column("x"));
                await catalog.DropColumnAsync("dbo", "t", "x");
                cycles++;
            }
            catch (SqlCatalogException exception)
            {
                failure = exception;
            }
        }

        // Assert: refused for the record size, naming the dropped columns and the remedy.
        failure.ShouldNotBeNull();
        cycles.ShouldBeGreaterThan(1_000);
        failure.Message.ShouldStartWith("The definition of table 'dbo.t' encodes to ", Case.Sensitive);
        failure.Message.ShouldContain($"It keeps the physical positions of {cycles} dropped columns, which are never reused (#1241); " +
            "recreating the table and copying its rows (CREATE TABLE, INSERT ... SELECT) reclaims them.", Case.Sensitive);

        // The refused alteration changed nothing, in memory or on reopen.
        catalog.TryGetTable("dbo", "t", out var table).ShouldBeTrue();
        table.Columns.Select(column => column.Name).ShouldBe(["id", "note"]);
        table.DroppedColumnOrdinals.Count.ShouldBe(cycles);
        using var reopenedStorage = SqlStorage.Open(Copy(data), Copy(journal), new MemoryStream());
        SqlCatalog.Open(reopenedStorage).TryGetTable("dbo", "t", out var persisted).ShouldBeTrue();
        persisted.Columns.Select(column => column.Name).ShouldBe(["id", "note"]);
        persisted.DroppedColumnOrdinals.ShouldBe(Enumerable.Range(0, cycles).Select(cycle => cycle + 2));
    }

    /// <summary>The fields of a table record before its versioned extension.</summary>
    private static DatabaseKeyWriter TableRecordPrefix(long objectId, string schema, string name, params (string Name, bool Nullable)[] columns)
    {
        var writer = new DatabaseKeyWriter();
        writer.AppendInt32(1).AppendInt64(objectId).AppendString(schema, Collation.Binary).AppendString(name, Collation.Binary)
            .AppendInt32(columns.Length);
        foreach (var (column, nullable) in columns)
        {
            writer.AppendString(column, Collation.Binary).AppendInt8((sbyte)DatabaseType.Int32)
                .AppendInt32(-1).AppendInt32(-1).AppendInt32(-1).AppendBoolean(nullable).AppendNull();
        }

        // No primary key; ad-hoc ownership.
        writer.AppendInt32(0).AppendInt8((sbyte)DatabaseObjectOwner.Adhoc).AppendNull();
        return writer;
    }

    private static void Insert(SqlStorage storage, byte[] record)
    {
        using var transaction = storage.BeginTransaction();
        storage.InsertRow(transaction, record);
        transaction.Commit();
    }

    private static MemoryStream Copy(MemoryStream source)
    {
        var copy = new MemoryStream();
        copy.Write(source.ToArray());
        copy.Position = 0;
        return copy;
    }
}
