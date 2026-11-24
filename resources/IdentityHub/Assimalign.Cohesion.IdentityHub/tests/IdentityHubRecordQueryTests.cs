using System;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.IdentityHub.Tests;

public class IdentityHubRecordQueryTests
{
    [Fact(DisplayName = "Cohesion Test [IdentityHub] - All: Should select every record with the default page size")]
    public void All_WithNoArguments_ShouldSelectEveryRecordFromTheFirstPage()
    {
        // Act
        IdentityHubRecordQuery query = IdentityHubRecordQuery.All();

        // Assert
        query.Index.ShouldBeNull();
        query.Value.ShouldBeNull();
        query.Limit.ShouldBe(IdentityHubRecordQuery.DefaultLimit);
        query.Continuation.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - ByIndex: Should select the records that list the index value")]
    public void ByIndex_WithAnIndexAndValue_ShouldKeepTheSelection()
    {
        // Act
        IdentityHubRecordQuery query = IdentityHubRecordQuery.ByIndex("owner", "alice", limit: 25, continuation: "token");

        // Assert
        query.Index.ShouldBe(new IdentityHubRecordIndexName("owner"));
        query.Value.ShouldBe(new IdentityHubRecordKey("alice"));
        query.Limit.ShouldBe(25);
        query.Continuation.ShouldBe("token");
    }

    [Theory(DisplayName = "Cohesion Test [IdentityHub] - All: Should accept a page size from 1 to the maximum")]
    [InlineData(1)]
    [InlineData(IdentityHubRecordQuery.MaxLimit)]
    public void All_WithALimitInRange_ShouldKeepIt(int limit)
    {
        // Act
        IdentityHubRecordQuery all = IdentityHubRecordQuery.All(limit);
        IdentityHubRecordQuery byIndex = IdentityHubRecordQuery.ByIndex("owner", "alice", limit);

        // Assert
        all.Limit.ShouldBe(limit);
        byIndex.Limit.ShouldBe(limit);
    }

    [Theory(DisplayName = "Cohesion Test [IdentityHub] - All: Should refuse a page size out of range")]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(IdentityHubRecordQuery.MaxLimit + 1)]
    public void All_WithALimitOutOfRange_ShouldThrowArgumentOutOfRangeException(int limit)
    {
        // Act & Assert
        Should.Throw<ArgumentOutOfRangeException>(() => IdentityHubRecordQuery.All(limit));
        Should.Throw<ArgumentOutOfRangeException>(() => IdentityHubRecordQuery.ByIndex("owner", "alice", limit));
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - All: Should refuse an empty continuation")]
    public void All_WithAnEmptyContinuation_ShouldThrowArgumentException()
    {
        // Act
        ArgumentException all = Should.Throw<ArgumentException>(() => IdentityHubRecordQuery.All(continuation: string.Empty));
        ArgumentException byIndex = Should.Throw<ArgumentException>(
            () => IdentityHubRecordQuery.ByIndex("owner", "alice", continuation: string.Empty));

        // Assert
        all.ParamName.ShouldBe("continuation");
        byIndex.ParamName.ShouldBe("continuation");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - ByIndex: Should refuse a default index or value")]
    public void ByIndex_WithADefaultIndexOrValue_ShouldThrowArgumentException()
    {
        // Act
        ArgumentException noIndex = Should.Throw<ArgumentException>(() => IdentityHubRecordQuery.ByIndex(default, "alice"));
        ArgumentException noValue = Should.Throw<ArgumentException>(() => IdentityHubRecordQuery.ByIndex("owner", default));

        // Assert
        noIndex.ParamName.ShouldBe("index");
        noValue.ParamName.ShouldBe("value");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - After: Should keep the selection and page size for the next page")]
    public void After_WithAContinuation_ShouldReturnTheSameQueryForTheNextPage()
    {
        // Arrange
        IdentityHubRecordQuery all = IdentityHubRecordQuery.All(limit: 10);
        IdentityHubRecordQuery byIndex = IdentityHubRecordQuery.ByIndex("owner", "alice", limit: 5, continuation: "first");

        // Act
        IdentityHubRecordQuery nextAll = all.After("token");
        IdentityHubRecordQuery nextByIndex = byIndex.After("second");

        // Assert
        nextAll.ShouldNotBeSameAs(all);
        nextAll.Index.ShouldBeNull();
        nextAll.Limit.ShouldBe(10);
        nextAll.Continuation.ShouldBe("token");
        all.Continuation.ShouldBeNull();
        nextByIndex.Index.ShouldBe(byIndex.Index);
        nextByIndex.Value.ShouldBe(byIndex.Value);
        nextByIndex.Limit.ShouldBe(5);
        nextByIndex.Continuation.ShouldBe("second");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - After: Should refuse a null or empty continuation")]
    public void After_WithANullOrEmptyContinuation_ShouldThrowArgumentException()
    {
        // Arrange
        IdentityHubRecordQuery query = IdentityHubRecordQuery.All();

        // Act & Assert
        Should.Throw<ArgumentNullException>(() => query.After(null!));
        Should.Throw<ArgumentException>(() => query.After(string.Empty));
    }
}
