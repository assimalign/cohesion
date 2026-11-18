using System;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Fabric.Tests;

public class ValueObjectTests
{
    [Theory(DisplayName = "Cohesion Test [Fabric] - ResourceId: Should accept every address form")]
    [InlineData("/environments/prod")]
    [InlineData("/environments/prod/groups/orders-team")]
    [InlineData("/environments/prod/applications/orders")]
    [InlineData("/environments/prod/operations/op-41")]
    [InlineData("/environments/prod/applications/orders/providers/Cohesion.Workloads/workloads/orders-api")]
    [InlineData("/environments/prod/applications/identity/providers/Cohesion.Workloads/workloads/identity.store")]
    public void ResourceId_ValidAddress_ShouldBeAccepted(string value)
    {
        // Act
        var valid = ResourceId.TryCreate(value, out var id);

        // Assert
        valid.ShouldBeTrue();
        id.Value.ShouldBe(value);
    }

    [Theory(DisplayName = "Cohesion Test [Fabric] - ResourceId: Should refuse a malformed address")]
    [InlineData("")]
    [InlineData("environments/prod")]
    [InlineData("/environments/Prod")]
    [InlineData("/environments/prod/")]
    [InlineData("/environments/prod/widgets/a")]
    [InlineData("/environments/prod/applications/orders/providers/Cohesion.Workloads/workloads")]
    [InlineData("/environments/prod/applications/orders/providers/cohesion.workloads/workloads/a")]
    [InlineData("/environments/prod/applications/orders/providers/Cohesion.Workloads/workloads/a/extra")]
    public void ResourceId_MalformedAddress_ShouldBeRefused(string value)
    {
        // Act & Assert
        ResourceId.TryCreate(value, out _).ShouldBeFalse();
        Should.Throw<ArgumentException>(() => new ResourceId(value));
    }

    [Fact(DisplayName = "Cohesion Test [Fabric] - ResourceUid: Should accept only version 7")]
    public void ResourceUid_Version_ShouldBeSeven()
    {
        // Arrange
        var created = ResourceUid.CreateVersion7();

        // Act & Assert
        created.Value.Version.ShouldBe(7);
        ResourceUid.TryCreate(Guid.NewGuid(), out _).ShouldBeFalse();
        ResourceUid.TryCreate(Guid.Empty, out _).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Fabric] - ResourceTypeName: Should split namespace and type")]
    public void ResourceTypeName_Valid_ShouldSplitNamespaceAndType()
    {
        // Act
        var type = new ResourceTypeName("Kubernetes.Networking/networkPolicies");

        // Assert
        type.Namespace.ShouldBe("Kubernetes.Networking");
        type.Type.ShouldBe("networkPolicies");
        ResourceTypeName.TryCreate("Cohesion/Workloads", out _).ShouldBeFalse();
        ResourceTypeName.TryCreate("/workloads", out _).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Fabric] - ResourceName: Should tell a child name from a user name")]
    public void ResourceName_Dotted_ShouldBeChildName()
    {
        // Act & Assert
        new ResourceName("identity.store").IsChildName.ShouldBeTrue();
        new ResourceName("orders-api").IsChildName.ShouldBeFalse();
        ResourceName.TryCreate("orders-", out _).ShouldBeFalse();
        ResourceName.TryCreate("1orders", out _).ShouldBeFalse();
        ResourceName.TryCreate("a..b", out _).ShouldBeFalse();
    }

    [Theory(DisplayName = "Cohesion Test [Fabric] - ApiVersion: Should accept a real date with an optional preview suffix")]
    [InlineData("2026-12-01", true)]
    [InlineData("2026-12-01-preview", true)]
    [InlineData("2026-02-30", false)]
    [InlineData("2026-12", false)]
    [InlineData("v1", false)]
    public void ApiVersion_Candidate_ShouldValidateAsDate(string value, bool expected)
    {
        // Act & Assert
        ApiVersion.TryCreate(value, out _).ShouldBe(expected);
    }

    [Fact(DisplayName = "Cohesion Test [Fabric] - PlatformName: Should accept one lowercase label")]
    public void PlatformName_Candidate_ShouldBeOneLabel()
    {
        // Act & Assert
        PlatformName.TryCreate("kubernetes", out _).ShouldBeTrue();
        PlatformName.TryCreate("Kubernetes", out _).ShouldBeFalse();
        PlatformName.TryCreate("in.process", out _).ShouldBeFalse();
    }
}
