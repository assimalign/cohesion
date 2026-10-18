using System;
using System.Linq;
using System.Threading.Tasks;
using Assimalign.Cohesion.Database.Transactions;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Documents.Catalog.Tests;

public sealed partial class DocumentCatalogTests
{
    [Theory(DisplayName = "Cohesion Test [Documents.Catalog] - Legacy index records are refused before recovery writes (#1135)")]
    [InlineData((byte)4)]
    [InlineData((byte)5)]
    public async Task EnsureIndexFormat_V1IndexRecord_ShouldRefuseWithoutWrites(byte kind)
    {
        // Arrange: hand-build a v1 header on an otherwise current physical record.
        await using var fixture = await Fixture.Create();
        await fixture.CreateIndex("by_a", "a");
        using (var records = fixture.Storage.GetUnitIterator(kind == 5 ? 1UL : 0UL))
        {
            while (records.MoveNext())
            {
                var unit = records.Current;
                if (unit.Data.Span[16] != kind) { continue; }
                var bytes = unit.Data.ToArray();
                bytes[17] = 1;
                using var bracket = fixture.Storage.BeginTransaction();
                fixture.Storage.UpdateEntry(bracket, unit.PageId, unit.SlotIndex, bytes);
                bracket.Commit();
                break;
            }
        }
        var before = fixture.Data.ToArray();
        var journal = fixture.Journal.ToArray();

        // Act.
        var error = Should.Throw<DocumentCatalogException>(() => DocumentCatalog.EnsureIndexFormat(fixture.Storage));

        // Assert.
        error.Message.ShouldContain("found 1, supported 2");
        error.Message.ShouldContain("Recreate");
        fixture.Data.ToArray().ShouldBe(before);
        fixture.Journal.ToArray().ShouldBe(journal);
    }

    [Fact(DisplayName = "Cohesion Test [Documents.Catalog] - Index paths compare by value and cannot change (#1135)")]
    public void Equals_EquivalentPathLists_ShouldHaveValueEquality()
    {
        // Arrange.
        string[] paths = ["a", "b.c"];
        var id = Guid.NewGuid();
        var first = new DocumentIndexMetadata(id, "ix", paths, true, 10);
        var second = new DocumentIndexMetadata(id, "ix", ["a", "b.c"], true, 10);

        // Act.
        paths[0] = "changed";

        // Assert.
        first.ShouldBe(second);
        first.GetHashCode().ShouldBe(second.GetHashCode());
        new[] { first }.SequenceEqual([second]).ShouldBeTrue();
        first.Paths.ShouldBe(["a", "b.c"]);
    }
}
