using System.IO;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Storage;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Catalog.Tests;

public sealed class SqlCatalogKeyOrderCorruptionTests
{
    [Theory(DisplayName = "Cohesion Test [Sql.Catalog] - Directed index codec: invalid orders, missing orders and earlier extensions are coded (#1631)")]
    [InlineData(3, 4, "CS06059")]
    [InlineData(3, -1, "CS06059")]
    [InlineData(3, -2, "CS06059")]
    [InlineData(2, 0, "CS06060")]
    public void Open_InvalidKeyOrderRecord_ShouldRefuseWithCatalogCode(int extension, int order, string code)
    {
        // Arrange: a complete index header; -2 deliberately omits the mandatory order byte.
        using var storage = SqlStorage.Create(new MemoryStream(), new MemoryStream(), new MemoryStream(), "key-order-codec");
        var writer = new DatabaseKeyWriter().AppendInt32(5).AppendInt64(1).AppendString("ix", Collation.Binary)
            .AppendBoolean(false).AppendInt32(1).AppendString("a", Collation.Binary)
            .AppendInt8((sbyte)DatabaseObjectOwner.Adhoc).AppendNull().AppendInt32(extension).AppendBoolean(false)
            .AppendNull().AppendInt32(0).AppendInt32(0);
        if (order != -2) { writer.AppendInt8((sbyte)order); }
        using (var bracket = storage.BeginTransaction())
        {
            storage.InsertRow(bracket, writer.ToArray());
            bracket.Commit();
        }

        // Act
        var failure = Should.Throw<SqlCatalogException>(() => SqlCatalog.Open(storage));

        // Assert
        failure.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == code);
    }
}
