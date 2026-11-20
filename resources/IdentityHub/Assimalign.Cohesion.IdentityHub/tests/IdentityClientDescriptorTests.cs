using System;
using System.Linq;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.IdentityHub.Tests;

public class IdentityClientDescriptorTests
{
    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Constructor: Should start as a registration that only exposes an API")]
    public void Constructor_ShouldStartEmptyWithTheDefaultLifetime()
    {
        // Act
        var descriptor = new IdentityClientDescriptor();

        // Assert
        descriptor.Id.Value.ShouldBeNull();
        descriptor.DisplayName.ShouldBeNull();
        descriptor.Description.ShouldBeNull();
        descriptor.Grants.ShouldBe(IdentityClientGrants.None);
        descriptor.AccessTokenLifetime.ShouldBe(IdentityClient.DefaultAccessTokenLifetime);
        descriptor.Redirects.ShouldBeEmpty();
        descriptor.Credentials.ShouldBeEmpty();
        descriptor.Audiences.ShouldBeEmpty();
        descriptor.Scopes.ShouldBeEmpty();
        descriptor.Roles.ShouldBeEmpty();
        descriptor.Permissions.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - From: Should copy the contents of a registration")]
    public void From_WithARegistration_ShouldCopyItsContents()
    {
        // Arrange
        IdentityClient client = IdentityClient.Create(new()
        {
            Id = "orders",
            DisplayName = "Orders",
            Description = "Takes orders.",
            Grants = IdentityClientGrants.AuthorizationCode | IdentityClientGrants.ClientCredentials,
            AccessTokenLifetime = TimeSpan.FromMinutes(5),
            Redirects = { IdentityClientRedirect.Native("com.example.orders:/callback") },
            Credentials = { IdentityClientSecretCredential.Generate("primary", out _) },
            Audiences = { "api://orders" },
            Scopes = { IdentityClientScope.Create("orders.read") },
            Roles = { IdentityClientRole.Create("orders.process", IdentityClientRoleMembers.Applications) },
            Permissions = { IdentityClientPermission.Application("orders", "orders.process") },
        });

        // Act
        IdentityClientDescriptor descriptor = IdentityClientDescriptor.From(client);

        // Assert
        descriptor.Id.ShouldBe(client.Id);
        descriptor.DisplayName.ShouldBe("Orders");
        descriptor.Description.ShouldBe("Takes orders.");
        descriptor.Grants.ShouldBe(client.Grants);
        descriptor.AccessTokenLifetime.ShouldBe(TimeSpan.FromMinutes(5));
        descriptor.Redirects.ShouldBe(client.Redirects);
        descriptor.Credentials.ShouldBe(client.Credentials);
        descriptor.Audiences.ShouldBe(client.Audiences);
        descriptor.Scopes.ShouldBe(client.Scopes);
        descriptor.Roles.ShouldBe(client.Roles);
        descriptor.Permissions.ShouldBe(client.Permissions);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - From: Should give a copy that changes no registration")]
    public void From_ThenEditing_ShouldLeaveTheRegistrationAsItWas()
    {
        // Arrange
        IdentityClient client = TestClients.Api();
        IdentityClientDescriptor descriptor = IdentityClientDescriptor.From(client);

        // Act
        descriptor.DisplayName = "Renamed";
        descriptor.Scopes.Add(IdentityClientScope.Create("orders.write"));
        IdentityClient replacement = IdentityClient.Create(descriptor);

        // Assert
        client.DisplayName.ShouldBe("Orders API");
        client.Scopes.Count.ShouldBe(1);
        replacement.DisplayName.ShouldBe("Renamed");
        replacement.Scopes.Select(scope => scope.Name.Value).ShouldBe(["orders.read", "orders.write"]);
        replacement.Id.ShouldBe(client.Id);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - From: Should not copy what a registry sets")]
    public void From_WithALockedOrStoredRegistration_ShouldProduceOneThatIsNeither()
    {
        // Arrange
        IdentityClient locked = TestClients.Api().AsLocked();
        IdentityClient stored = TestClients.Api().AsStored(new IdentityHubRecordVersion("5"));

        // Act
        IdentityClient fromLocked = IdentityClient.Create(IdentityClientDescriptor.From(locked));
        IdentityClient fromStored = IdentityClient.Create(IdentityClientDescriptor.From(stored));

        // Assert
        fromLocked.IsLocked.ShouldBeFalse();
        fromStored.Version.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - From: Should read another implementation that leaves a list null")]
    public void From_WithAnImplementationThatLeavesAListNull_ShouldTreatItAsEmpty()
    {
        // Arrange
        var foreign = new StubIdentityClient
        {
            Id = "foreign",
            DisplayName = "Foreign",
            Redirects = null!,
            Credentials = null!,
            Audiences = null!,
            Scopes = null!,
            Roles = null!,
            Permissions = null!,
        };

        // Act
        IdentityClientDescriptor descriptor = IdentityClientDescriptor.From(foreign);

        // Assert
        descriptor.Id.Value.ShouldBe("foreign");
        descriptor.Redirects.ShouldBeEmpty();
        descriptor.Permissions.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - From: Should refuse a null registration")]
    public void From_WithANullRegistration_ShouldThrowArgumentNullException()
    {
        // Act
        ArgumentNullException exception = Should.Throw<ArgumentNullException>(() => IdentityClientDescriptor.From(null!));

        // Assert
        exception.ParamName.ShouldBe("client");
    }
}
