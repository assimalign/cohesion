using System;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.IdentityHub.Tests;

public class IdentityHubWriteResultTests
{
    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Default: Should not read as a written record")]
    public void Default_OfAResult_ShouldReportNoWrite()
    {
        // Act
        IdentityHubWriteResult result = default;

        // Assert
        result.Status.ShouldBe(IdentityHubWriteStatus.None);
        result.IsWritten.ShouldBeFalse();
        result.Version.Value.ShouldBeNull();
        result.ConflictingIndex.Value.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Written: Should carry the record's version")]
    public void Written_WithAVersion_ShouldReportTheWriteAndItsVersion()
    {
        // Act
        IdentityHubWriteResult result = IdentityHubWriteResult.Written("42");

        // Assert
        result.Status.ShouldBe(IdentityHubWriteStatus.Written);
        result.IsWritten.ShouldBeTrue();
        result.Version.Value.ShouldBe("42");
        result.ConflictingIndex.Value.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Written: Should refuse a default version")]
    public void Written_WithADefaultVersion_ShouldThrowArgumentException()
    {
        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(() => IdentityHubWriteResult.Written(default));

        // Assert
        exception.ParamName.ShouldBe("version");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - KeyConflict: Should report a refused add")]
    public void KeyConflict_OfAResult_ShouldReportTheConflictAndNoVersion()
    {
        // Act
        IdentityHubWriteResult result = IdentityHubWriteResult.KeyConflict;

        // Assert
        result.Status.ShouldBe(IdentityHubWriteStatus.KeyConflict);
        result.IsWritten.ShouldBeFalse();
        result.Version.Value.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - VersionConflict: Should report a refused replace")]
    public void VersionConflict_OfAResult_ShouldReportTheConflictAndNoVersion()
    {
        // Act
        IdentityHubWriteResult result = IdentityHubWriteResult.VersionConflict;

        // Assert
        result.Status.ShouldBe(IdentityHubWriteStatus.VersionConflict);
        result.IsWritten.ShouldBeFalse();
        result.Version.Value.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - IndexConflict: Should name the index that refused the write")]
    public void IndexConflict_WithAnIndex_ShouldReportTheConflictAndTheIndex()
    {
        // Act
        IdentityHubWriteResult result = IdentityHubWriteResult.IndexConflict("user-code");

        // Assert
        result.Status.ShouldBe(IdentityHubWriteStatus.IndexConflict);
        result.IsWritten.ShouldBeFalse();
        result.ConflictingIndex.Value.ShouldBe("user-code");
        result.Version.Value.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - IndexConflict: Should refuse a default index")]
    public void IndexConflict_WithADefaultIndex_ShouldThrowArgumentException()
    {
        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(() => IdentityHubWriteResult.IndexConflict(default));

        // Assert
        exception.ParamName.ShouldBe("index");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Equals: Should compare the status, version, and index")]
    public void Equals_WithAnotherResult_ShouldCompareEveryPart()
    {
        // Act & Assert
        IdentityHubWriteResult.Written("42").ShouldBe(IdentityHubWriteResult.Written("42"));
        IdentityHubWriteResult.Written("42").ShouldNotBe(IdentityHubWriteResult.Written("43"));
        IdentityHubWriteResult.KeyConflict.ShouldBe(IdentityHubWriteResult.KeyConflict);
        IdentityHubWriteResult.KeyConflict.ShouldNotBe(IdentityHubWriteResult.VersionConflict);
        IdentityHubWriteResult.IndexConflict("alias").ShouldNotBe(IdentityHubWriteResult.IndexConflict("owner"));
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Status: Should keep its stable member values")]
    public void Status_Members_ShouldKeepTheirValues()
    {
        // Assert
        ((int)IdentityHubWriteStatus.None).ShouldBe(0);
        ((int)IdentityHubWriteStatus.Written).ShouldBe(1);
        ((int)IdentityHubWriteStatus.KeyConflict).ShouldBe(2);
        ((int)IdentityHubWriteStatus.VersionConflict).ShouldBe(3);
        ((int)IdentityHubWriteStatus.IndexConflict).ShouldBe(4);
    }
}
