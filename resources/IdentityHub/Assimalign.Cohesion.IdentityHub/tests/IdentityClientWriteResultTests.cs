using System;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.IdentityHub.Tests;

public class IdentityClientWriteResultTests
{
    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Written: Should carry the stored version and count as applied")]
    public void Written_WithAVersion_ShouldCarryItAndBeApplied()
    {
        // Act
        IdentityClientWriteResult result = IdentityClientWriteResult.Written("7");

        // Assert
        result.Status.ShouldBe(IdentityClientWriteStatus.Written);
        result.Version.Value.ShouldBe("7");
        result.ConflictingAudience.Value.ShouldBeNull();
        result.IsApplied.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Written: Should refuse a default version")]
    public void Written_WithADefaultVersion_ShouldThrowArgumentException()
    {
        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(() => IdentityClientWriteResult.Written(default));

        // Assert
        exception.ParamName.ShouldBe("version");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Removed: Should count as applied and carry no version")]
    public void Removed_ShouldBeAppliedWithNoVersion()
    {
        // Act
        IdentityClientWriteResult result = IdentityClientWriteResult.Removed;

        // Assert
        result.Status.ShouldBe(IdentityClientWriteStatus.Removed);
        result.Version.Value.ShouldBeNull();
        result.IsApplied.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Locked, IdConflict, VersionConflict: Should be refusals")]
    public void Refusals_ShouldNotBeApplied()
    {
        // Act
        IdentityClientWriteResult locked = IdentityClientWriteResult.Locked;
        IdentityClientWriteResult idConflict = IdentityClientWriteResult.IdConflict;
        IdentityClientWriteResult versionConflict = IdentityClientWriteResult.VersionConflict;

        // Assert
        locked.Status.ShouldBe(IdentityClientWriteStatus.Locked);
        idConflict.Status.ShouldBe(IdentityClientWriteStatus.IdConflict);
        versionConflict.Status.ShouldBe(IdentityClientWriteStatus.VersionConflict);
        locked.IsApplied.ShouldBeFalse();
        idConflict.IsApplied.ShouldBeFalse();
        versionConflict.IsApplied.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - AudienceConflict: Should name the audience when it is known")]
    public void AudienceConflict_WithAndWithoutAnAudience_ShouldCarryWhatIsKnown()
    {
        // Act
        IdentityClientWriteResult named = IdentityClientWriteResult.AudienceConflict("api://orders");
        IdentityClientWriteResult unnamed = IdentityClientWriteResult.AudienceConflict();

        // Assert
        named.Status.ShouldBe(IdentityClientWriteStatus.AudienceConflict);
        named.ConflictingAudience.Value.ShouldBe("api://orders");
        named.IsApplied.ShouldBeFalse();
        unnamed.Status.ShouldBe(IdentityClientWriteStatus.AudienceConflict);
        unnamed.ConflictingAudience.Value.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Default: Should be no outcome")]
    public void Default_ShouldBeNoOutcome()
    {
        // Act
        IdentityClientWriteResult result = default;

        // Assert
        result.Status.ShouldBe(IdentityClientWriteStatus.None);
        result.IsApplied.ShouldBeFalse();
    }
}
