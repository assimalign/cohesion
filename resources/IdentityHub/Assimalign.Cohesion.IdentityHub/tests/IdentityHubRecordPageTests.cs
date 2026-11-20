using System;
using System.Collections.Generic;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.IdentityHub.Tests;

public class IdentityHubRecordPageTests
{
    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Constructor: Should keep the records and the continuation")]
    public void Constructor_WithRecordsAndAContinuation_ShouldExposeThem()
    {
        // Arrange
        IdentityHubStoredRecord first = Stored("a");
        IdentityHubStoredRecord second = Stored("b");

        // Act
        var page = new IdentityHubRecordPage([first, second], "token");
        var last = new IdentityHubRecordPage([first], null);

        // Assert
        page.Records.ShouldBe([first, second]);
        page.Continuation.ShouldBe("token");
        last.Continuation.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Constructor: Should allow an empty page that is not the last")]
    public void Constructor_WithNoRecordsAndAContinuation_ShouldKeepTheContinuation()
    {
        // Act
        var page = new IdentityHubRecordPage(Array.Empty<IdentityHubStoredRecord>(), "token");

        // Assert
        page.Records.ShouldBeEmpty();
        page.Continuation.ShouldBe("token");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Constructor: Should copy the list of records")]
    public void Constructor_WhenTheCallerChangesItsListAfterwards_ShouldKeepTheRecords()
    {
        // Arrange
        IdentityHubStoredRecord first = Stored("a");
        var records = new List<IdentityHubStoredRecord> { first };
        var page = new IdentityHubRecordPage(records, null);

        // Act
        records.Add(Stored("b"));

        // Assert
        page.Records.ShouldBe([first]);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Constructor: Should refuse a null list, a null record, and an empty continuation")]
    public void Constructor_WithANullListOrRecordOrEmptyContinuation_ShouldThrow()
    {
        // Act
        ArgumentNullException nullList = Should.Throw<ArgumentNullException>(() => new IdentityHubRecordPage(null!, null));
        ArgumentException nullRecord = Should.Throw<ArgumentException>(() => new IdentityHubRecordPage([Stored("a"), null!], null));
        ArgumentException emptyToken = Should.Throw<ArgumentException>(() => new IdentityHubRecordPage([Stored("a")], string.Empty));

        // Assert
        nullList.ParamName.ShouldBe("records");
        nullRecord.ParamName.ShouldBe("records");
        emptyToken.ParamName.ShouldBe("continuation");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Empty: Should be a last page with no record")]
    public void Empty_OfAPage_ShouldHoldNoRecordAndNoContinuation()
    {
        // Act
        IdentityHubRecordPage page = IdentityHubRecordPage.Empty;

        // Assert
        page.Records.ShouldBeEmpty();
        page.Continuation.ShouldBeNull();
        IdentityHubRecordPage.Empty.ShouldBeSameAs(page);
    }

    private static IdentityHubStoredRecord Stored(string key)
    {
        return new IdentityHubStoredRecord(new IdentityHubRecord(key, ReadOnlyMemory<byte>.Empty), "1");
    }
}
