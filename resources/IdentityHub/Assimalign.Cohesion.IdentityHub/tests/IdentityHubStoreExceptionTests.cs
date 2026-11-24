using System;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.IdentityHub.Tests;

public class IdentityHubStoreExceptionTests
{
    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Constructor: Should carry the category, message, and cause")]
    public void Constructor_WithACodeMessageAndCause_ShouldExposeThem()
    {
        // Arrange
        var cause = new TimeoutException();

        // Act
        var exception = new IdentityHubStoreException(IdentityHubErrorCode.StoreUnavailable, "The store timed out.", cause)
        {
            Set = "clients",
            Key = "client-1",
        };
        var bare = new IdentityHubStoreException(IdentityHubErrorCode.Unknown, "The store failed.");

        // Assert
        exception.ShouldBeAssignableTo<IdentityHubException>();
        exception.Code.ShouldBe(IdentityHubErrorCode.StoreUnavailable);
        exception.Message.ShouldBe("The store timed out.");
        exception.InnerException.ShouldBeSameAs(cause);
        exception.Set.ShouldBe(new IdentityHubRecordSetName("clients"));
        exception.Key.ShouldBe(new IdentityHubRecordKey("client-1"));
        bare.Code.ShouldBe(IdentityHubErrorCode.Unknown);
        bare.InnerException.ShouldBeNull();
        bare.Set.ShouldBeNull();
        bare.Key.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - ContinuationInvalid: Should name the record set and the category")]
    public void ContinuationInvalid_WithASet_ShouldCarryTheSetAndCode()
    {
        // Arrange
        var cause = new FormatException();

        // Act
        IdentityHubStoreException exception = IdentityHubStoreException.ContinuationInvalid("clients", cause);
        IdentityHubStoreException bare = IdentityHubStoreException.ContinuationInvalid(default);

        // Assert
        exception.Code.ShouldBe(IdentityHubErrorCode.StoreContinuationInvalid);
        exception.Set.ShouldBe(new IdentityHubRecordSetName("clients"));
        exception.Key.ShouldBeNull();
        exception.InnerException.ShouldBeSameAs(cause);
        exception.Message.ShouldNotBeNullOrWhiteSpace();
        bare.Set.ShouldBeNull();
        bare.InnerException.ShouldBeNull();
        bare.Message.ShouldBe(exception.Message);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - RecordCorrupt: Should name the record set and key and the category")]
    public void RecordCorrupt_WithASetAndKey_ShouldCarryThemInTheMessageAndProperties()
    {
        // Arrange
        var cause = new FormatException();

        // Act
        IdentityHubStoreException exception = IdentityHubStoreException.RecordCorrupt("clients", "client-1", cause);
        IdentityHubStoreException bare = IdentityHubStoreException.RecordCorrupt(default, default);

        // Assert
        exception.Code.ShouldBe(IdentityHubErrorCode.StoreRecordCorrupt);
        exception.Set.ShouldBe(new IdentityHubRecordSetName("clients"));
        exception.Key.ShouldBe(new IdentityHubRecordKey("client-1"));
        exception.InnerException.ShouldBeSameAs(cause);
        exception.Message.ShouldContain("'clients'", Case.Sensitive);
        exception.Message.ShouldContain("'client-1'", Case.Sensitive);
        bare.Code.ShouldBe(IdentityHubErrorCode.StoreRecordCorrupt);
        bare.Set.ShouldBeNull();
        bare.Key.ShouldBeNull();
        bare.InnerException.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - ErrorCode: Should keep its stable member values")]
    public void ErrorCode_Members_ShouldKeepTheirValues()
    {
        // Assert
        ((int)IdentityHubErrorCode.Unknown).ShouldBe(0);
        ((int)IdentityHubErrorCode.StoreUnavailable).ShouldBe(1);
        ((int)IdentityHubErrorCode.StoreRecordCorrupt).ShouldBe(2);
        ((int)IdentityHubErrorCode.StoreContinuationInvalid).ShouldBe(3);
    }
}
