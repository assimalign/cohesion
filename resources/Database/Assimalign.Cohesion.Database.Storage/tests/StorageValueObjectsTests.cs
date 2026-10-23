using System;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Storage.Tests;

public class StorageValueObjectsTests
{
    [Fact(DisplayName = "Cohesion Test [Name] - Equality: Should compare by ordinal value")]
    public void NameEquality_ShouldCompareByOrdinalValue()
    {
        Name a = "users";
        Name b = "users";
        Name c = "Users";

        (a == b).ShouldBeTrue();
        a.Equals(b).ShouldBeTrue();
        (a != c).ShouldBeTrue();
        a.Equals(c).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [PageId] - Conversion: Should roundtrip long conversions")]
    public void PageIdConversions_ShouldRoundtripLong()
    {
        PageId pageId = 42L;
        long value = pageId;

        value.ShouldBe(42L);
        pageId.ToString().ShouldBe("42");
        pageId.CompareTo((PageId)43L).ShouldBeLessThan(0);
    }

    [Fact(DisplayName = "Cohesion Test [StorageId] - NewId: Should produce unique values")]
    public void StorageIdNewId_ShouldProduceUniqueValues()
    {
        StorageId id1 = StorageId.NewId();
        StorageId id2 = StorageId.NewId();

        (id1 != id2).ShouldBeTrue();
        ((Guid)id1).ShouldNotBe(Guid.Empty);
        ((Guid)id2).ShouldNotBe(Guid.Empty);
    }

    [Fact(DisplayName = "Cohesion Test [Address] - Append: Should grow path and compute offset")]
    public void AddressAppend_ShouldGrowPathAndComputeAbsoluteOffset()
    {
        var address = Address.Root(100).Append(20).Append(5);

        address.Depth.ShouldBe(3);
        address.GetOffset(0).ShouldBe(100);
        address.GetOffset(1).ShouldBe(20);
        address.GetOffset(2).ShouldBe(5);
        address.ToAbsoluteOffset().ShouldBe(125);
    }

    [Fact(DisplayName = "Cohesion Test [Address] - GetOffset: Should throw for invalid depth")]
    public void AddressGetOffset_ShouldThrowForInvalidDepth()
    {
        var address = Address.Root(1);

        Should.Throw<ArgumentOutOfRangeException>(() => address.GetOffset(1)).GetType().ShouldBe(typeof(ArgumentOutOfRangeException));
    }
}
