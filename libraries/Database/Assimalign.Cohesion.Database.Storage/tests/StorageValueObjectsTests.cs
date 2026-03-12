using System;
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

        Assert.True(a == b);
        Assert.True(a.Equals(b));
        Assert.True(a != c);
        Assert.False(a.Equals(c));
    }

    [Fact(DisplayName = "Cohesion Test [PageId] - Conversion: Should roundtrip long conversions")]
    public void PageIdConversions_ShouldRoundtripLong()
    {
        PageId pageId = 42L;
        long value = pageId;

        Assert.Equal(42L, value);
        Assert.Equal("42", pageId.ToString());
        Assert.True(pageId.CompareTo((PageId)43L) < 0);
    }

    [Fact(DisplayName = "Cohesion Test [StorageId] - NewId: Should produce unique values")]
    public void StorageIdNewId_ShouldProduceUniqueValues()
    {
        StorageId id1 = StorageId.NewId();
        StorageId id2 = StorageId.NewId();

        Assert.True(id1 != id2);
        Assert.NotEqual(Guid.Empty, (Guid)id1);
        Assert.NotEqual(Guid.Empty, (Guid)id2);
    }

    [Fact(DisplayName = "Cohesion Test [Address] - Append: Should grow path and compute offset")]
    public void AddressAppend_ShouldGrowPathAndComputeAbsoluteOffset()
    {
        var address = Address.Root(100).Append(20).Append(5);

        Assert.Equal(3, address.Depth);
        Assert.Equal(100, address.GetOffset(0));
        Assert.Equal(20, address.GetOffset(1));
        Assert.Equal(5, address.GetOffset(2));
        Assert.Equal(125, address.ToAbsoluteOffset());
    }

    [Fact(DisplayName = "Cohesion Test [Address] - GetOffset: Should throw for invalid depth")]
    public void AddressGetOffset_ShouldThrowForInvalidDepth()
    {
        var address = Address.Root(1);

        Assert.Throws<ArgumentOutOfRangeException>(() => address.GetOffset(1));
    }
}
