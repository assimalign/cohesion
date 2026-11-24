using System;
using System.Collections.Generic;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.IdentityHub.Tests;

public class IdentityClientPageTests
{
    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Constructor: Should copy the registrations and keep the continuation")]
    public void Constructor_WithRegistrations_ShouldCopyThem()
    {
        // Arrange
        var clients = new List<IdentityClient> { TestClients.Api() };

        // Act
        var page = new IdentityClientPage(clients, "next");
        clients.Add(TestClients.Web());

        // Assert
        page.Clients.Count.ShouldBe(1);
        page.Continuation.ShouldBe("next");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Constructor: Should accept an empty last page")]
    public void Constructor_WithNoRegistrationsAndNoContinuation_ShouldBeAnEmptyLastPage()
    {
        // Act
        var page = new IdentityClientPage([], null);

        // Assert
        page.Clients.ShouldBeEmpty();
        page.Continuation.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Constructor: Should refuse a null list, a null registration, and an empty continuation")]
    public void Constructor_WithAnInvalidArgument_ShouldThrow()
    {
        // Act
        ArgumentNullException nullList = Should.Throw<ArgumentNullException>(() => new IdentityClientPage(null!, null));
        ArgumentException nullEntry = Should.Throw<ArgumentException>(() => new IdentityClientPage([null!], null));
        ArgumentException emptyContinuation = Should.Throw<ArgumentException>(() => new IdentityClientPage([], string.Empty));

        // Assert
        nullList.ParamName.ShouldBe("clients");
        nullEntry.ParamName.ShouldBe("clients");
        emptyContinuation.ParamName.ShouldBe("continuation");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Limits: Should match the store's page limits")]
    public void Limits_ShouldMatchTheStoreQueryLimits()
    {
        // Assert
        IdentityClientPage.DefaultLimit.ShouldBe(IdentityHubRecordQuery.DefaultLimit);
        IdentityClientPage.MaxLimit.ShouldBe(IdentityHubRecordQuery.MaxLimit);
    }
}
