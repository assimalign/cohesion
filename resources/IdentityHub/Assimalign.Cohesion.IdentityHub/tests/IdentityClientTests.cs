using System;
using System.Linq;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.IdentityHub.Tests;

/// <summary>
/// An app registration is checked as a whole when it is created, so one that exists is consistent.
/// </summary>
public class IdentityClientTests
{
    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Create: Should snapshot every part of a descriptor")]
    public void Create_WithACompleteDescriptor_ShouldSnapshotEveryPart()
    {
        // Arrange
        IdentityClientSecretCredential secret = IdentityClientSecretCredential.Generate("primary", out _);
        var descriptor = new IdentityClientDescriptor
        {
            Id = "orders",
            DisplayName = "Orders",
            Description = "Takes orders.\nShips them.",
            Grants = IdentityClientGrants.AuthorizationCode | IdentityClientGrants.ClientCredentials | IdentityClientGrants.RefreshToken,
            AccessTokenLifetime = TimeSpan.FromMinutes(20),
            Redirects = { IdentityClientRedirect.Web("https://orders.example/signin") },
            Credentials = { secret },
            Audiences = { "api://orders" },
            Scopes = { IdentityClientScope.Create("orders.read") },
            Roles = { IdentityClientRole.Create("orders.process", IdentityClientRoleMembers.Applications) },
            Permissions = { IdentityClientPermission.Delegated("billing-api", "invoices.read") },
        };

        // Act
        IdentityClient client = IdentityClient.Create(descriptor);

        // Assert
        client.Id.Value.ShouldBe("orders");
        client.DisplayName.ShouldBe("Orders");
        client.Description.ShouldBe("Takes orders.\nShips them.");
        client.Grants.ShouldBe(descriptor.Grants);
        client.AccessTokenLifetime.ShouldBe(TimeSpan.FromMinutes(20));
        client.Redirects.Single().Uri.ShouldBe("https://orders.example/signin");
        client.Credentials.Single().ShouldBeSameAs(secret);
        client.Audiences.Single().Value.ShouldBe("api://orders");
        client.Scopes.Single().Name.Value.ShouldBe("orders.read");
        client.Roles.Single().Name.Value.ShouldBe("orders.process");
        client.Permissions.Single().ShouldBe(IdentityClientPermission.Delegated("billing-api", "invoices.read"));
        client.IsConfidential.ShouldBeTrue();
        client.IsLocked.ShouldBeFalse();
        client.Version.ShouldBeNull();
        client.ToString().ShouldBe("orders");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Create: Should apply the defaults of a registration that only names itself")]
    public void Create_WithOnlyAnIdentifier_ShouldApplyTheDefaults()
    {
        // Act
        IdentityClient client = IdentityClient.Create(new() { Id = "orders", Description = string.Empty });

        // Assert
        client.DisplayName.ShouldBe("orders");
        client.Description.ShouldBeNull();
        client.Grants.ShouldBe(IdentityClientGrants.None);
        client.AccessTokenLifetime.ShouldBe(TimeSpan.FromHours(1));
        client.IsConfidential.ShouldBeFalse();
        client.Redirects.ShouldBeEmpty();
        client.Credentials.ShouldBeEmpty();
        client.Audiences.ShouldBeEmpty();
        client.Scopes.ShouldBeEmpty();
        client.Roles.ShouldBeEmpty();
        client.Permissions.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Create: Should not change when the descriptor changes afterwards")]
    public void Create_ThenChangingTheDescriptor_ShouldNotChangeTheRegistration()
    {
        // Arrange
        var descriptor = new IdentityClientDescriptor { Id = "orders-api", Audiences = { "api://orders" } };
        IdentityClient client = IdentityClient.Create(descriptor);

        // Act
        descriptor.DisplayName = "Changed";
        descriptor.Audiences.Add("api://changed");

        // Assert
        client.DisplayName.ShouldBe("orders-api");
        client.Audiences.Count.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Create: Should refuse a null descriptor and a missing identifier")]
    public void Create_WithNoDescriptorOrIdentifier_ShouldThrow()
    {
        // Act
        ArgumentNullException nullDescriptor = Should.Throw<ArgumentNullException>(() => IdentityClient.Create(null!));
        ArgumentException noId = Should.Throw<ArgumentException>(() => IdentityClient.Create(new IdentityClientDescriptor()));

        // Assert
        nullDescriptor.ParamName.ShouldBe("descriptor");
        noId.ParamName.ShouldBe("descriptor");
    }

    [Theory(DisplayName = "Cohesion Test [IdentityHub] - Create: Should refuse a display name outside its rule and name the registration")]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("Orders\nAPI")]
    public void Create_WithADisplayNameOutsideItsRule_ShouldThrowArgumentException(string displayName)
    {
        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(
            () => IdentityClient.Create(new() { Id = "orders", DisplayName = displayName }));

        // Assert
        exception.ParamName.ShouldBe("descriptor");
        exception.Message.ShouldContain("'orders'", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Create: Should accept text at its limit and refuse it beyond")]
    public void Create_WithTextAtAndBeyondItsLimit_ShouldAcceptTheLimitOnly()
    {
        // Arrange
        string longestName = new('n', IdentityClient.MaxDisplayNameLength);
        string longestDescription = new('d', IdentityClient.MaxDescriptionLength);

        // Act
        IdentityClient atLimit = IdentityClient.Create(new() { Id = "orders", DisplayName = longestName, Description = longestDescription });
        ArgumentException longName = Should.Throw<ArgumentException>(
            () => IdentityClient.Create(new() { Id = "orders", DisplayName = longestName + "n" }));
        ArgumentException longDescription = Should.Throw<ArgumentException>(
            () => IdentityClient.Create(new() { Id = "orders", Description = longestDescription + "d" }));
        ArgumentException controlDescription = Should.Throw<ArgumentException>(
            () => IdentityClient.Create(new() { Id = "orders", Description = "bell\u0007" }));

        // Assert
        atLimit.DisplayName.ShouldBe(longestName);
        atLimit.Description.ShouldBe(longestDescription);
        longName.Message.ShouldContain("display name");
        longDescription.Message.ShouldContain("description");
        controlDescription.Message.ShouldContain("description");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Create: Should refuse a grant the hub does not define")]
    public void Create_WithAnUndefinedGrant_ShouldThrowArgumentException()
    {
        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(
            () => IdentityClient.Create(new() { Id = "orders", Grants = (IdentityClientGrants)16 }));

        // Assert
        exception.Message.ShouldContain("'orders'", Case.Sensitive);
    }

    [Theory(DisplayName = "Cohesion Test [IdentityHub] - Create: Should refuse an access token lifetime that is not positive or is longer than a day")]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData((24 * 60 * 60) + 1)]
    public void Create_WithALifetimeOutOfRange_ShouldThrowArgumentOutOfRangeException(int seconds)
    {
        // Act
        ArgumentOutOfRangeException exception = Should.Throw<ArgumentOutOfRangeException>(
            () => IdentityClient.Create(new() { Id = "orders", AccessTokenLifetime = TimeSpan.FromSeconds(seconds) }));

        // Assert
        exception.ParamName.ShouldBe("descriptor");
        exception.ActualValue.ShouldBe(TimeSpan.FromSeconds(seconds));
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Create: Should accept the longest access token lifetime")]
    public void Create_WithTheLongestLifetime_ShouldKeepIt()
    {
        // Act
        IdentityClient client = IdentityClient.Create(new() { Id = "orders", AccessTokenLifetime = IdentityClient.MaxAccessTokenLifetime });

        // Assert
        IdentityClient.MaxAccessTokenLifetime.ShouldBe(TimeSpan.FromHours(24));
        IdentityClient.DefaultAccessTokenLifetime.ShouldBe(TimeSpan.FromHours(1));
        client.AccessTokenLifetime.ShouldBe(TimeSpan.FromHours(24));
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Create: Should refuse the authorization code grant without a redirect")]
    public void Create_WithAuthorizationCodeAndNoRedirect_ShouldThrowArgumentException()
    {
        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(
            () => IdentityClient.Create(new() { Id = "orders-web", Grants = IdentityClientGrants.AuthorizationCode }));

        // Assert
        exception.Message.ShouldContain("redirect");
        exception.Message.ShouldContain("'orders-web'", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Create: Should refuse a redirect on a registration that does not use the authorization code grant")]
    public void Create_WithARedirectAndNoAuthorizationCode_ShouldThrowArgumentException()
    {
        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(() => IdentityClient.Create(new()
        {
            Id = "orders-device",
            Grants = IdentityClientGrants.DeviceCode,
            Redirects = { IdentityClientRedirect.Web("https://orders.example/signin") },
        }));

        // Assert
        exception.Message.ShouldContain("redirect");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Create: Should refuse the client credentials grant without a credential")]
    public void Create_WithClientCredentialsAndNoCredential_ShouldThrowArgumentException()
    {
        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(
            () => IdentityClient.Create(new() { Id = "orders-daemon", Grants = IdentityClientGrants.ClientCredentials }));

        // Assert
        exception.Message.ShouldContain("credential");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Create: Should refuse refresh tokens without a grant that issues them")]
    public void Create_WithRefreshTokenAlone_ShouldThrowArgumentException()
    {
        // Arrange
        IdentityClientSecretCredential secret = IdentityClientSecretCredential.Generate("primary", out _);

        // Act
        ArgumentException alone = Should.Throw<ArgumentException>(
            () => IdentityClient.Create(new() { Id = "orders", Grants = IdentityClientGrants.RefreshToken }));
        ArgumentException withClientCredentials = Should.Throw<ArgumentException>(() => IdentityClient.Create(new()
        {
            Id = "orders",
            Grants = IdentityClientGrants.RefreshToken | IdentityClientGrants.ClientCredentials,
            Credentials = { secret },
        }));
        IdentityClient withDeviceCode = IdentityClient.Create(new()
        {
            Id = "orders",
            Grants = IdentityClientGrants.RefreshToken | IdentityClientGrants.DeviceCode,
        });

        // Assert
        alone.Message.ShouldContain("refresh");
        withClientCredentials.Message.ShouldContain("refresh");
        withDeviceCode.Grants.HasFlag(IdentityClientGrants.RefreshToken).ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Create: Should refuse a scope or a role on a registration that exposes no audience")]
    public void Create_WithAScopeOrRoleAndNoAudience_ShouldThrowArgumentException()
    {
        // Act
        ArgumentException scope = Should.Throw<ArgumentException>(
            () => IdentityClient.Create(new() { Id = "orders-api", Scopes = { IdentityClientScope.Create("orders.read") } }));
        ArgumentException role = Should.Throw<ArgumentException>(() => IdentityClient.Create(new()
        {
            Id = "orders-api",
            Roles = { IdentityClientRole.Create("orders.process", IdentityClientRoleMembers.Users) },
        }));

        // Assert
        scope.Message.ShouldContain("audience");
        role.Message.ShouldContain("audience");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Create: Should refuse the same redirect, credential, audience, scope, role, or permission twice")]
    public void Create_WithARepeatedEntry_ShouldThrowArgumentExceptionNamingIt()
    {
        // Arrange
        IdentityClientSecretCredential first = IdentityClientSecretCredential.Generate("primary", out _);
        IdentityClientSecretCredential second = IdentityClientSecretCredential.Generate("primary", out _);

        // Act
        ArgumentException redirect = Should.Throw<ArgumentException>(() => IdentityClient.Create(new()
        {
            Id = "orders",
            Grants = IdentityClientGrants.AuthorizationCode,
            Redirects =
            {
                IdentityClientRedirect.Web("https://orders.example/signin"),
                IdentityClientRedirect.SinglePage("https://orders.example/signin"),
            },
        }));
        ArgumentException credential = Should.Throw<ArgumentException>(
            () => IdentityClient.Create(new() { Id = "orders", Credentials = { first, second } }));
        ArgumentException audience = Should.Throw<ArgumentException>(
            () => IdentityClient.Create(new() { Id = "orders", Audiences = { "api://orders", "api://orders" } }));
        ArgumentException scope = Should.Throw<ArgumentException>(() => IdentityClient.Create(new()
        {
            Id = "orders",
            Audiences = { "api://orders" },
            Scopes = { IdentityClientScope.Create("orders.read"), IdentityClientScope.Create("orders.read", "Again") },
        }));
        ArgumentException role = Should.Throw<ArgumentException>(() => IdentityClient.Create(new()
        {
            Id = "orders",
            Audiences = { "api://orders" },
            Roles =
            {
                IdentityClientRole.Create("orders.process", IdentityClientRoleMembers.Users),
                IdentityClientRole.Create("orders.process", IdentityClientRoleMembers.Applications),
            },
        }));
        ArgumentException permission = Should.Throw<ArgumentException>(() => IdentityClient.Create(new()
        {
            Id = "orders",
            Permissions =
            {
                IdentityClientPermission.Delegated("billing-api", "invoices.read"),
                IdentityClientPermission.Delegated("billing-api", "invoices.read"),
            },
        }));

        // Assert
        redirect.Message.ShouldContain("https://orders.example/signin", Case.Sensitive);
        credential.Message.ShouldContain("'primary'", Case.Sensitive);
        audience.Message.ShouldContain("'api://orders'", Case.Sensitive);
        scope.Message.ShouldContain("'orders.read'", Case.Sensitive);
        role.Message.ShouldContain("'orders.process'", Case.Sensitive);
        permission.Message.ShouldContain("billing-api/invoices.read", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Create: Should allow a scope and a role of one name, and one name under both permission kinds")]
    public void Create_WithOneNameAsScopeAndRole_ShouldCreate()
    {
        // Act
        IdentityClient client = IdentityClient.Create(new()
        {
            Id = "orders-api",
            Audiences = { "api://orders" },
            Scopes = { IdentityClientScope.Create("orders.read") },
            Roles = { IdentityClientRole.Create("orders.read", IdentityClientRoleMembers.Applications) },
            Permissions =
            {
                IdentityClientPermission.Delegated("orders-api", "orders.read"),
                IdentityClientPermission.Application("orders-api", "orders.read"),
            },
        });

        // Assert
        client.Scopes.Count.ShouldBe(1);
        client.Roles.Count.ShouldBe(1);
        client.Permissions.Count.ShouldBe(2);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Create: Should refuse an empty entry in a list")]
    public void Create_WithAnEmptyEntry_ShouldThrowArgumentExceptionNamingTheList()
    {
        // Act
        ArgumentException redirect = Should.Throw<ArgumentException>(() => IdentityClient.Create(new()
        {
            Id = "orders",
            Grants = IdentityClientGrants.AuthorizationCode,
            Redirects = { default },
        }));
        ArgumentException credential = Should.Throw<ArgumentException>(
            () => IdentityClient.Create(new() { Id = "orders", Credentials = { null! } }));
        ArgumentException audience = Should.Throw<ArgumentException>(
            () => IdentityClient.Create(new() { Id = "orders", Audiences = { default } }));
        ArgumentException scope = Should.Throw<ArgumentException>(
            () => IdentityClient.Create(new() { Id = "orders", Audiences = { "api://orders" }, Scopes = { null! } }));
        ArgumentException role = Should.Throw<ArgumentException>(
            () => IdentityClient.Create(new() { Id = "orders", Audiences = { "api://orders" }, Roles = { null! } }));
        ArgumentException permission = Should.Throw<ArgumentException>(
            () => IdentityClient.Create(new() { Id = "orders", Permissions = { default } }));

        // Assert
        redirect.Message.ShouldContain("'Redirects'", Case.Sensitive);
        credential.Message.ShouldContain("'Credentials'", Case.Sensitive);
        audience.Message.ShouldContain("'Audiences'", Case.Sensitive);
        scope.Message.ShouldContain("'Scopes'", Case.Sensitive);
        role.Message.ShouldContain("'Roles'", Case.Sensitive);
        permission.Message.ShouldContain("'Permissions'", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Create: Should accept every list at its limit and refuse one entry more")]
    public void Create_WithListsAtAndBeyondTheirLimits_ShouldAcceptTheLimitsOnly()
    {
        // Act
        IdentityClient atLimit = IdentityClient.Create(Maximal());
        ArgumentException redirects = Should.Throw<ArgumentException>(() =>
        {
            IdentityClientDescriptor descriptor = Maximal();
            descriptor.Redirects.Add(IdentityClientRedirect.Web("https://orders.example/one-more"));
            IdentityClient.Create(descriptor);
        });
        ArgumentException credentials = Should.Throw<ArgumentException>(() =>
        {
            IdentityClientDescriptor descriptor = Maximal();
            descriptor.Credentials.Add(IdentityClientSecretCredential.Generate("one-more", out _));
            IdentityClient.Create(descriptor);
        });
        ArgumentException audiences = Should.Throw<ArgumentException>(() =>
        {
            IdentityClientDescriptor descriptor = Maximal();
            descriptor.Audiences.Add("api://one-more");
            IdentityClient.Create(descriptor);
        });
        ArgumentException scopes = Should.Throw<ArgumentException>(() =>
        {
            IdentityClientDescriptor descriptor = Maximal();
            descriptor.Scopes.Add(IdentityClientScope.Create("one.more"));
            IdentityClient.Create(descriptor);
        });
        ArgumentException roles = Should.Throw<ArgumentException>(() =>
        {
            IdentityClientDescriptor descriptor = Maximal();
            descriptor.Roles.Add(IdentityClientRole.Create("one.more", IdentityClientRoleMembers.Users));
            IdentityClient.Create(descriptor);
        });
        ArgumentException permissions = Should.Throw<ArgumentException>(() =>
        {
            IdentityClientDescriptor descriptor = Maximal();
            descriptor.Permissions.Add(IdentityClientPermission.Delegated("billing-api", "one.more"));
            IdentityClient.Create(descriptor);
        });

        // Assert
        atLimit.Redirects.Count.ShouldBe(64);
        atLimit.Credentials.Count.ShouldBe(16);
        atLimit.Audiences.Count.ShouldBe(8);
        atLimit.Scopes.Count.ShouldBe(64);
        atLimit.Roles.Count.ShouldBe(64);
        atLimit.Permissions.Count.ShouldBe(128);
        redirects.Message.ShouldContain("'Redirects' holds at most 64", Case.Sensitive);
        credentials.Message.ShouldContain("'Credentials' holds at most 16", Case.Sensitive);
        audiences.Message.ShouldContain("'Audiences' holds at most 8", Case.Sensitive);
        scopes.Message.ShouldContain("'Scopes' holds at most 64", Case.Sensitive);
        roles.Message.ShouldContain("'Roles' holds at most 64", Case.Sensitive);
        permissions.Message.ShouldContain("'Permissions' holds at most 128", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Create: Should refuse a permission on itself that names no scope it exposes")]
    public void Create_WithADelegatedPermissionOnItselfThatNamesNoScope_ShouldThrowArgumentException()
    {
        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(() => IdentityClient.Create(new()
        {
            Id = "orders-api",
            Audiences = { "api://orders" },
            Scopes = { IdentityClientScope.Create("orders.read") },
            Permissions = { IdentityClientPermission.Delegated("orders-api", "orders.write") },
        }));

        // Assert
        exception.Message.ShouldContain("'orders.write'", Case.Sensitive);
        exception.Message.ShouldContain("scope");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Create: Should refuse an application permission on itself for a role open to users only")]
    public void Create_WithAnApplicationPermissionOnItselfForAUserRole_ShouldThrowArgumentException()
    {
        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(() => IdentityClient.Create(new()
        {
            Id = "orders-api",
            Audiences = { "api://orders" },
            Roles = { IdentityClientRole.Create("orders.approve", IdentityClientRoleMembers.Users) },
            Permissions = { IdentityClientPermission.Application("orders-api", "orders.approve") },
        }));

        // Assert
        exception.Message.ShouldContain("'orders.approve'", Case.Sensitive);
        exception.Message.ShouldContain("role");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Create: Should leave a permission on another registration unresolved")]
    public void Create_WithAPermissionOnAnotherRegistration_ShouldNotResolveIt()
    {
        // Act
        IdentityClient client = IdentityClient.Create(new()
        {
            Id = "orders-web",
            Permissions = { IdentityClientPermission.Delegated("no-such-api", "no.such.scope") },
        });

        // Assert
        client.Permissions.Single().Resource.Value.ShouldBe("no-such-api");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Exposes: Should match a scope for a delegated permission and an application role for an application one")]
    public void Exposes_WithEachPermissionKind_ShouldMatchWhatTheRegistrationExposes()
    {
        // Arrange
        IdentityClient api = IdentityClient.Create(new()
        {
            Id = "orders-api",
            Audiences = { "api://orders" },
            Scopes = { IdentityClientScope.Create("orders.read") },
            Roles =
            {
                IdentityClientRole.Create("orders.process", IdentityClientRoleMembers.Applications),
                IdentityClientRole.Create("orders.approve", IdentityClientRoleMembers.Users),
            },
        });

        // Act + Assert
        api.Exposes(IdentityClientPermission.Delegated("orders-api", "orders.read")).ShouldBeTrue();
        api.Exposes(IdentityClientPermission.Delegated("orders-api", "orders.process")).ShouldBeFalse();
        api.Exposes(IdentityClientPermission.Application("orders-api", "orders.process")).ShouldBeTrue();
        api.Exposes(IdentityClientPermission.Application("orders-api", "orders.approve")).ShouldBeFalse();
        api.Exposes(IdentityClientPermission.Application("orders-api", "orders.read")).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - From: Should return an IdentityClient as it is and copy another implementation through validation")]
    public void From_WithEachImplementation_ShouldReturnAValidatedRegistration()
    {
        // Arrange
        IdentityClient known = TestClients.Api();
        var foreign = new StubIdentityClient
        {
            Id = "foreign-api",
            DisplayName = "Foreign",
            Audiences = [new IdentityClientAudience("api://foreign")],
            IsLocked = true,
            Version = new IdentityHubRecordVersion("9"),
        };
        var invalid = new StubIdentityClient { Id = "foreign-web", DisplayName = "Foreign", Grants = IdentityClientGrants.AuthorizationCode };

        // Act
        IdentityClient same = IdentityClient.From(known);
        IdentityClient copied = IdentityClient.From(foreign);
        ArgumentException refused = Should.Throw<ArgumentException>(() => IdentityClient.From(invalid));

        // Assert
        same.ShouldBeSameAs(known);
        copied.Id.Value.ShouldBe("foreign-api");
        copied.Audiences.Single().Value.ShouldBe("api://foreign");
        copied.IsLocked.ShouldBeFalse();
        copied.Version.ShouldBeNull();
        refused.Message.ShouldContain("'foreign-web'", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - AsLocked, AsStored: Should set what a registry says about a registration and nothing else")]
    public void AsLockedAndAsStored_ShouldSetOnlyTheRegistryState()
    {
        // Arrange
        IdentityClient client = TestClients.Api();

        // Act
        IdentityClient locked = client.AsStored(new IdentityHubRecordVersion("3")).AsLocked();
        IdentityClient stored = locked.AsStored(new IdentityHubRecordVersion("4"));

        // Assert
        locked.IsLocked.ShouldBeTrue();
        locked.Version.ShouldBeNull();
        stored.IsLocked.ShouldBeFalse();
        stored.Version!.Value.Value.ShouldBe("4");
        stored.Audiences.ShouldBeSameAs(client.Audiences);
        client.IsLocked.ShouldBeFalse();
        client.Version.ShouldBeNull();
    }

    /// <summary>
    /// A descriptor with every list at its limit.
    /// </summary>
    internal static IdentityClientDescriptor Maximal()
    {
        var descriptor = new IdentityClientDescriptor
        {
            Id = "orders",
            Grants = IdentityClientGrants.AuthorizationCode | IdentityClientGrants.ClientCredentials,
        };

        for (int index = 0; index < IdentityClient.MaxRedirectCount; index++)
        {
            descriptor.Redirects.Add(IdentityClientRedirect.Web($"https://orders.example/signin/{index}"));
        }

        for (int index = 0; index < IdentityClient.MaxCredentialCount; index++)
        {
            descriptor.Credentials.Add(IdentityClientSecretCredential.Generate($"secret-{index}", out _));
        }

        for (int index = 0; index < IdentityClient.MaxAudienceCount; index++)
        {
            descriptor.Audiences.Add($"api://orders/{index}");
        }

        for (int index = 0; index < IdentityClient.MaxScopeCount; index++)
        {
            descriptor.Scopes.Add(IdentityClientScope.Create($"orders.scope.{index}"));
        }

        for (int index = 0; index < IdentityClient.MaxRoleCount; index++)
        {
            descriptor.Roles.Add(IdentityClientRole.Create($"orders.role.{index}", IdentityClientRoleMembers.Applications));
        }

        for (int index = 0; index < IdentityClient.MaxPermissionCount; index++)
        {
            descriptor.Permissions.Add(IdentityClientPermission.Delegated("billing-api", $"invoices.scope.{index}"));
        }

        return descriptor;
    }
}
