using System.IO;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Storage;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Catalog.Tests;

public sealed class SqlCatalogFunctionCallCorruptionTests
{
    [Theory(DisplayName = "Cohesion Test [Sql.Catalog] - Function identity runs refuse invalid and unbounded expansion")]
    [InlineData(false, -1, 1)]
    [InlineData(false, 7, 1)]
    [InlineData(false, 1, 0)]
    [InlineData(true, 1, 0)]
    [InlineData(false, 1, -1)]
    [InlineData(false, 1, int.MaxValue)]
    [InlineData(true, 1, int.MaxValue)]
    [InlineData(false, 2, 4)]
    [InlineData(true, 2, 4)]
    public void Open_InvalidFunctionIdentityRuns_ShouldRefuseInvalidExpansion(bool check, int runs, int repeat)
    {
        // Arrange: ABS(1) has six characters, so no valid identity vector can contain more than six calls.
        using var storage = SqlStorage.Create(new MemoryStream(), new MemoryStream(), new MemoryStream(), "invalid-function-runs");
        var record = TableRecord(check).AppendInt32(runs);
        for (int run = 0; run < runs; run++)
        {
            record.AppendString("ABS(INT32)", Collation.Binary).AppendInt32(repeat);
        }
        Insert(storage, record.ToArray());

        // Act
        var failure = Should.Throw<SqlCatalogException>(() => SqlCatalog.Open(storage));

        // Assert
        failure.Message.ShouldContain("function-call");
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Catalog] - Function identity runs refuse blank identities")]
    [InlineData(false)]
    [InlineData(true)]
    public void Open_BlankFunctionIdentityRun_ShouldRefuseMalformedRecord(bool check)
    {
        // Arrange
        using var storage = SqlStorage.Create(new MemoryStream(), new MemoryStream(), new MemoryStream(), "blank-function-run");
        var record = TableRecord(check).AppendInt32(1).AppendString(" ", Collation.Binary).AppendInt32(1);
        Insert(storage, record.ToArray());

        // Act
        var failure = Should.Throw<SqlCatalogException>(() => SqlCatalog.Open(storage));

        // Assert
        failure.Message.ShouldContain("function-call");
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Catalog] - Function identity runs require every repetition count")]
    [InlineData(false)]
    [InlineData(true)]
    public void Open_TruncatedFunctionIdentityRun_ShouldRefuseMalformedRecord(bool check)
    {
        // Arrange
        using var storage = SqlStorage.Create(new MemoryStream(), new MemoryStream(), new MemoryStream(), "truncated-function-run");
        var record = TableRecord(check).AppendInt32(1).AppendString("ABS(INT32)", Collation.Binary);
        var location = Insert(storage, record.ToArray());

        // Act / Assert
        var failure = Should.Throw<SqlCatalogException>(() => SqlCatalog.Open(storage));
        var corruption = StorageCorruptionException.Find(failure).ShouldNotBeNull();
        corruption.PageId.ShouldBe(location.PageId);
        corruption.SlotIndex.ShouldBe(location.SlotIndex);
        corruption.InnerException.ShouldBeOfType<DatabaseTypeException>();
    }

    private static DatabaseKeyWriter TableRecord(bool check)
    {
        var writer = new DatabaseKeyWriter();
        writer.AppendInt32(1).AppendInt64(1).AppendString("dbo", Collation.Binary).AppendString("t", Collation.Binary)
            .AppendInt32(1).AppendString("id", Collation.Binary).AppendInt8((sbyte)DatabaseType.Int32)
            .AppendInt32(-1).AppendInt32(-1).AppendInt32(-1).AppendBoolean(true);
        if (check) { writer.AppendNull(); }
        else { writer.AppendString("ABS(1)", Collation.Binary); }
        writer.AppendInt32(0).AppendInt8((sbyte)DatabaseObjectOwner.Adhoc).AppendNull()
            .AppendInt32(6).AppendInt32(check ? 1 : 0);
        if (check)
        {
            writer.AppendString("ck", Collation.Binary).AppendInt8((sbyte)SqlCatalogConstraintKind.Check)
                .AppendInt32(0).AppendNull().AppendNull().AppendInt32(0).AppendString("ABS(1)", Collation.Binary).AppendInt8(0);
        }
        writer.AppendInt32(-1).AppendInt32(0).AppendNull();
        if (check) { writer.AppendInt32(0); }
        return writer;
    }

    private static (PageId PageId, int SlotIndex) Insert(SqlStorage storage, byte[] record)
    {
        using var transaction = storage.BeginTransaction();
        var location = storage.InsertRow(transaction, record);
        transaction.Commit();
        return location;
    }
}
