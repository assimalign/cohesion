using System;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.IdentityHub.Tests;

public class IdentityObjectWriteResultTests
{
    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Written: Should carry the stored version of an identity object and count as applied")]
    public void Written_WithAVersion_ShouldCarryItAndBeApplied()
    {
        // Act
        IdentityObjectWriteResult result = IdentityObjectWriteResult.Written("7");

        // Assert
        result.Status.ShouldBe(IdentityObjectWriteStatus.Written);
        result.Version.Value.ShouldBe("7");
        result.IsApplied.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Written: Should refuse a default version for an identity object")]
    public void Written_WithADefaultVersion_ShouldThrowArgumentException()
    {
        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(() => IdentityObjectWriteResult.Written(default));

        // Assert
        exception.ParamName.ShouldBe("version");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Removed: Should count a removed identity object as applied with no version")]
    public void Removed_ShouldBeAppliedWithNoVersion()
    {
        // Act
        IdentityObjectWriteResult result = IdentityObjectWriteResult.Removed;

        // Assert
        result.Status.ShouldBe(IdentityObjectWriteStatus.Removed);
        result.Version.Value.ShouldBeNull();
        result.IsApplied.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Locked, IdConflict, VersionConflict, NameConflict: Should be refusals of an identity object write")]
    public void Refusals_ShouldNotBeApplied()
    {
        // Act
        IdentityObjectWriteResult locked = IdentityObjectWriteResult.Locked;
        IdentityObjectWriteResult idConflict = IdentityObjectWriteResult.IdConflict;
        IdentityObjectWriteResult versionConflict = IdentityObjectWriteResult.VersionConflict;
        IdentityObjectWriteResult nameConflict = IdentityObjectWriteResult.NameConflict;

        // Assert
        locked.Status.ShouldBe(IdentityObjectWriteStatus.Locked);
        idConflict.Status.ShouldBe(IdentityObjectWriteStatus.IdConflict);
        versionConflict.Status.ShouldBe(IdentityObjectWriteStatus.VersionConflict);
        nameConflict.Status.ShouldBe(IdentityObjectWriteStatus.NameConflict);
        locked.IsApplied.ShouldBeFalse();
        idConflict.IsApplied.ShouldBeFalse();
        versionConflict.IsApplied.ShouldBeFalse();
        nameConflict.IsApplied.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Default: Should be no identity object write result")]
    public void Default_ShouldHaveNoStatus()
    {
        // Act
        IdentityObjectWriteResult result = default;

        // Assert
        result.Status.ShouldBe(IdentityObjectWriteStatus.None);
        result.IsApplied.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Equals: Should compare identity object write results by status and version")]
    public void Equals_WithTheSameStatusAndVersion_ShouldBeEqual()
    {
        // Act
        IdentityObjectWriteResult first = IdentityObjectWriteResult.Written("7");
        IdentityObjectWriteResult same = IdentityObjectWriteResult.Written("7");
        IdentityObjectWriteResult other = IdentityObjectWriteResult.Written("8");

        // Assert
        first.ShouldBe(same);
        first.ShouldNotBe(other);
        IdentityObjectWriteResult.Locked.ShouldBe(IdentityObjectWriteResult.Locked);
    }
}
