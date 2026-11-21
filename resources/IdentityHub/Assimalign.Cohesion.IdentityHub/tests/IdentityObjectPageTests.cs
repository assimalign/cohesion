using System;
using System.Collections.Generic;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.IdentityHub.Tests;

public class IdentityObjectPageTests
{
    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Constructor: Should copy the objects of an identity object page")]
    public void Constructor_WithItems_ShouldCopyThem()
    {
        // Arrange
        var items = new List<IdentityGroup> { IdentityGroup.Create(new() { Name = "orders-admins" }) };

        // Act
        var page = new IdentityObjectPage<IdentityGroup>(items, "next");
        items.Clear();

        // Assert
        page.Items.Count.ShouldBe(1);
        page.Items[0].Name.Value.ShouldBe("orders-admins");
        page.Continuation.ShouldBe("next");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Constructor: Should accept an empty identity object page that still continues")]
    public void Constructor_WithNoItemsAndAContinuation_ShouldKeepBoth()
    {
        // Act
        var page = new IdentityObjectPage<IdentityUser>([], "next");
        var last = new IdentityObjectPage<IdentityUser>([], null);

        // Assert
        page.Items.ShouldBeEmpty();
        page.Continuation.ShouldBe("next");
        last.Continuation.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Constructor: Should refuse null items, a null object, and an empty continuation on an identity object page")]
    public void Constructor_WithInvalidArguments_ShouldThrow()
    {
        // Act
        ArgumentNullException nullItems = Should.Throw<ArgumentNullException>(
            () => new IdentityObjectPage<IdentityUser>(null!, null));
        ArgumentException nullItem = Should.Throw<ArgumentException>(
            () => new IdentityObjectPage<IdentityUser>([null!], null));
        ArgumentException emptyContinuation = Should.Throw<ArgumentException>(
            () => new IdentityObjectPage<IdentityUser>([], string.Empty));

        // Assert
        nullItems.ParamName.ShouldBe("items");
        nullItem.ParamName.ShouldBe("items");
        emptyContinuation.ParamName.ShouldBe("continuation");
    }
}
