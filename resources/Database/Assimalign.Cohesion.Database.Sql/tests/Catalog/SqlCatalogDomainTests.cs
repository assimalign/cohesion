using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Sql.Storage;
using Assimalign.Cohesion.Database.Types;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Catalog.Tests;

/// <summary>
/// A column's domain name in its table record (table-record extension version 4, data-storage
/// format 7, phase E3): the column stores the domain's base type, and the catalog keeps the name
/// through every alteration and reopen so an opening engine binds the domain again.
/// </summary>
public sealed class SqlCatalogDomainTests
{
    [Fact(DisplayName = "Cohesion Test [Sql.Catalog] - Domains: a column's domain name survives ADD COLUMN, DROP COLUMN, a constraint change and a reopen")]
    public async Task DomainColumns_ShouldKeepTheirDomainAcrossAlterationsAndReopen()
    {
        // Arrange
        using var data = new MemoryStream();
        using var journal = new MemoryStream();
        using var storage = SqlStorage.Create(new NonClosingStream(data), new NonClosingStream(journal), new MemoryStream(), "domains");
        SqlCatalog catalog = SqlCatalog.Open(storage);
        await catalog.CreateTableAsync("dbo", "contacts",
        [
            new SqlCatalogColumn("id", new DatabaseTypeInfo(DatabaseType.Int64), isNullable: false),
            new SqlCatalogColumn("email", new DatabaseTypeInfo(DatabaseType.String, 320), collation: Collation.CaseInsensitive, domainName: "email"),
            new SqlCatalogColumn("note", new DatabaseTypeInfo(DatabaseType.String)),
        ], ["id"]);

        // Act
        await catalog.AddColumnAsync("dbo", "contacts", new SqlCatalogColumn("price", new DatabaseTypeInfo(DatabaseType.Decimal, null, 18, 2),
            domainName: "Money"));
        await catalog.DropColumnAsync("dbo", "contacts", "note");
        await catalog.AddConstraintAsync("dbo", "contacts",
            new SqlCatalogConstraint("ck_price", SqlCatalogConstraintKind.Check, [], checkExpression: "price > 0"), default);

        // Assert: live, then after a reopen without a checkpoint.
        catalog.TryGetTable("dbo", "contacts", out var live).ShouldBeTrue();
        AssertDomains(live);
        using var reopenedStorage = SqlStorage.Open(Copy(data), Copy(journal), new MemoryStream());
        SqlCatalog.Open(reopenedStorage).TryGetTable("dbo", "contacts", out var persisted).ShouldBeTrue();
        AssertDomains(persisted);

        static void AssertDomains(SqlCatalogTable table)
        {
            table.Columns.Select(column => column.DomainName).ShouldBe([null, "email", "Money"]);
            table.FindColumn("email").ShouldNotBeNull().Type.MaxLength.ShouldBe(320);
            table.FindColumn("email")!.Collation.ShouldBe(Collation.CaseInsensitive);
            table.FindColumn("price").ShouldNotBeNull().Type.Precision.ShouldBe(18);
            table.DroppedColumnOrdinals.ShouldBe([2]);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Catalog] - Domains: legacy extension versions are refused")]
    public void TableRecord_ExtensionVersionThree_ShouldRefuseUnsupportedVersion()
    {
        // Arrange: a table record as data-storage format 6 wrote it.
        using var storage = SqlStorage.Create(new MemoryStream(), new MemoryStream(), new MemoryStream(), "format-six");
        var record = TableRecordPrefix("legacy", ("id", false), ("note", true))
            .AppendInt32(3).AppendInt32(0)          // extension version 3, no constraints
            .AppendInt32(-1).AppendInt32(-1)        // no collation overrides
            .AppendInt32(0);                        // no dropped columns
        Insert(storage, record.ToArray());

        // Act / Assert
        var failure = Should.Throw<SqlCatalogException>(() => SqlCatalog.Open(storage));
        failure.Message.ShouldContain("Unsupported table-constraint metadata version 3", Case.Sensitive);
        failure.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "CS06060");
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Catalog] - Domains: a persisted blank domain name is refused at load")]
    public void TableRecord_BlankDomainName_ShouldBeRefusedAtLoad()
    {
        // Arrange
        using var storage = SqlStorage.Create(new MemoryStream(), new MemoryStream(), new MemoryStream(), "damaged");
        var record = TableRecordPrefix("damaged", ("id", false), ("note", true))
            .AppendInt32(7).AppendInt32(0).AppendInt32(-1).AppendInt32(-1).AppendInt32(0);
        record.AppendNull().AppendString(" ", Collation.Binary);
        Insert(storage, record.ToArray());

        // Act / Assert
        Should.Throw<SqlCatalogException>(() => SqlCatalog.Open(storage)).Message.ShouldContain("domain name of column 'note'");
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Catalog] - Domains: a column description refuses a blank domain name")]
    public void Column_BlankDomainName_ShouldBeRefused()
        => Should.Throw<ArgumentException>(() => new SqlCatalogColumn("c", new DatabaseTypeInfo(DatabaseType.String), domainName: " "))
            .ParamName.ShouldBe("domainName");

    /// <summary>The fields of a table record before its versioned extension.</summary>
    private static DatabaseKeyWriter TableRecordPrefix(string name, params (string Name, bool Nullable)[] columns)
    {
        var writer = new DatabaseKeyWriter();
        writer.AppendInt32(1).AppendInt64(1).AppendString("dbo", Collation.Binary).AppendString(name, Collation.Binary)
            .AppendInt32(columns.Length);
        foreach (var (column, nullable) in columns)
        {
            writer.AppendString(column, Collation.Binary).AppendInt8((sbyte)DatabaseType.String)
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
