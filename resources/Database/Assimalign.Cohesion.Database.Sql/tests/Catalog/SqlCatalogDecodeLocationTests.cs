using System.IO;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Sql.Storage;
using Assimalign.Cohesion.Database.Types;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Catalog.Tests;

public sealed class SqlCatalogDecodeLocationTests
{
    [Theory(DisplayName = "Cohesion Test [Sql.Catalog] - Load: confirmed malformed metadata reports page and slot (#1372)")]
    [InlineData(false)]
    [InlineData(true)]
    public void Open_MalformedRecord_ShouldRetainCatalogErrorAndReportLocation(bool truncated)
    {
        using var storage = SqlStorage.Create(new MemoryStream(), new MemoryStream(), new MemoryStream(), "decode");
        var bytes = truncated ? new byte[] { 255 } : new DatabaseKeyWriter().AppendInt32(255).ToArray();
        using var bracket = storage.BeginTransaction();
        var location = storage.InsertRow(bracket, bytes);
        bracket.Commit();

        var failure = Should.Throw<SqlCatalogException>(() => SqlCatalog.Open(storage));
        var corruption = StorageCorruptionException.Find(failure).ShouldNotBeNull();
        corruption.PageId.ShouldBe(location.PageId);
        corruption.SlotIndex.ShouldBe(location.SlotIndex);
        corruption.InnerException.ShouldNotBeNull();
        failure.Message.ShouldContain($"page {(long)location.PageId}", Case.Sensitive);
        failure.Message.ShouldContain($"slot {location.SlotIndex}", Case.Sensitive);

        using var repair = storage.BeginTransaction();
        storage.DeleteRow(repair, location.PageId, location.SlotIndex);
        repair.Commit();
        Should.NotThrow(() => SqlCatalog.Open(storage));
    }
}
