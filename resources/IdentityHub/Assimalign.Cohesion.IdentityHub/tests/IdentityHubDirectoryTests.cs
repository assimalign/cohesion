using System;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.IdentityHub.Tests;

/// <summary>
/// A directory is created once, over a store, with the objects code defines, and checks those
/// objects against each other before it serves any of them.
/// </summary>
public class IdentityHubDirectoryTests
{
    private readonly InMemoryIdentityHubStore _store = new();

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Create: Should create an empty directory when no descriptor is given")]
    public async Task Create_WithNoDescriptor_ShouldCreateAnEmptyDirectory()
    {
        // Act
        IdentityHubDirectory directory = IdentityHubDirectory.Create(_store);
        IdentityHubDirectory explicitNull = IdentityHubDirectory.Create(_store, null);

        // Assert
        directory.Users.Locked.ShouldBeEmpty();
        directory.Groups.Locked.ShouldBeEmpty();
        explicitNull.Users.Locked.ShouldBeEmpty();
        (await directory.Users.ListAsync(cancellationToken: CancellationToken.None)).Items.ShouldBeEmpty();
        (await directory.Groups.ListAsync(cancellationToken: CancellationToken.None)).Items.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Create: Should serve the users and groups of a descriptor locked")]
    public async Task Create_WithADescriptor_ShouldServeItsObjectsLocked()
    {
        // Arrange
        var descriptor = new IdentityHubDirectoryDescriptor();
        descriptor.Users.Add(TestDirectory.User("ada@contoso.example", "Ada Lovelace"));
        descriptor.Groups.Add(TestDirectory.Group("orders-admins"));

        // Act
        IIdentityHubDirectory directory = IdentityHubDirectory.Create(_store, descriptor);
        IdentityUser? user = await directory.Users.FindByNameAsync("ada@contoso.example", CancellationToken.None);
        IdentityGroup? group = await directory.Groups.FindAsync(IdentityGroupId.FromName("orders-admins"), CancellationToken.None);

        // Assert
        user.ShouldNotBeNull();
        user.IsLocked.ShouldBeTrue();
        user.DisplayName.ShouldBe("Ada Lovelace");
        group.ShouldNotBeNull();
        group.IsLocked.ShouldBeTrue();
        _store.CountHeld(new("users")).ShouldBe(0);
        _store.CountHeld(new("groups")).ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Create: Should keep the objects it was created with when the descriptor changes afterwards")]
    public void Create_WhenTheDescriptorChangesAfterwards_ShouldKeepWhatItWasCreatedWith()
    {
        // Arrange
        var descriptor = new IdentityHubDirectoryDescriptor();
        descriptor.Users.Add(TestDirectory.User("ada"));
        descriptor.Groups.Add(TestDirectory.Group("everyone"));
        IdentityHubDirectory directory = IdentityHubDirectory.Create(_store, descriptor);

        // Act
        descriptor.Users.Add(TestDirectory.User("grace"));
        descriptor.Groups.Clear();

        // Assert
        directory.Users.Locked.Count.ShouldBe(1);
        directory.Groups.Locked.Count.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Create: Should refuse a null store")]
    public void Create_WithANullStore_ShouldThrowArgumentNullException()
    {
        // Act
        ArgumentNullException exception = Should.Throw<ArgumentNullException>(
            () => IdentityHubDirectory.Create(null!, new IdentityHubDirectoryDescriptor()));

        // Assert
        exception.ParamName.ShouldBe("store");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Create: Should refuse a descriptor that holds a null user or a null group")]
    public void Create_WithANullObject_ShouldThrowArgumentException()
    {
        // Arrange
        var nullUser = new IdentityHubDirectoryDescriptor();
        nullUser.Users.Add(null!);
        var nullGroup = new IdentityHubDirectoryDescriptor();
        nullGroup.Groups.Add(null!);

        // Act
        ArgumentException user = Should.Throw<ArgumentException>(() => IdentityHubDirectory.Create(_store, nullUser));
        ArgumentException group = Should.Throw<ArgumentException>(() => IdentityHubDirectory.Create(_store, nullGroup));

        // Assert
        user.ParamName.ShouldBe("descriptor");
        user.Message.ShouldContain("null user");
        group.ParamName.ShouldBe("descriptor");
        group.Message.ShouldContain("null group");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Create: Should refuse two users with one identifier, naming the user")]
    public void Create_WithTwoUsersOfOneIdentifier_ShouldThrowArgumentExceptionNamingTheUser()
    {
        // Arrange: the second user is the first one renamed, so they share the identifier only.
        var descriptor = new IdentityHubDirectoryDescriptor();
        descriptor.Users.Add(TestDirectory.User("ada"));
        descriptor.Users.Add(TestDirectory.User("ada.lovelace", id: IdentityUserId.FromName("ada")));

        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(() => IdentityHubDirectory.Create(_store, descriptor));

        // Assert
        exception.ParamName.ShouldBe("descriptor");
        exception.Message.ShouldContain("defined twice");
        exception.Message.ShouldContain("ada.lovelace");
        exception.Message.ShouldContain(IdentityUserId.FromName("ada").ToString());
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Create: Should refuse two users with one name in any case, naming both")]
    public void Create_WithTwoUsersOfOneName_ShouldThrowArgumentExceptionNamingBoth()
    {
        // Arrange
        IdentityUserId other = IdentityUserId.New();
        var descriptor = new IdentityHubDirectoryDescriptor();
        descriptor.Users.Add(TestDirectory.User("Ada"));
        descriptor.Users.Add(TestDirectory.User("ADA", id: other));

        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(() => IdentityHubDirectory.Create(_store, descriptor));

        // Assert
        exception.ParamName.ShouldBe("descriptor");
        exception.Message.ShouldContain("User name 'ada'", Case.Sensitive);
        exception.Message.ShouldContain(IdentityUserId.FromName("ada").ToString());
        exception.Message.ShouldContain(other.ToString());
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Create: Should refuse two groups with one identifier or one name, naming the group")]
    public void Create_WithTwoGroupsOfOneIdentifierOrName_ShouldThrowArgumentException()
    {
        // Arrange
        var sameId = new IdentityHubDirectoryDescriptor();
        sameId.Groups.Add(TestDirectory.Group("everyone"));
        sameId.Groups.Add(TestDirectory.Group("all-staff", id: IdentityGroupId.FromName("everyone")));
        var sameName = new IdentityHubDirectoryDescriptor();
        sameName.Groups.Add(TestDirectory.Group("everyone"));
        sameName.Groups.Add(TestDirectory.Group("Everyone", id: IdentityGroupId.New()));

        // Act
        ArgumentException id = Should.Throw<ArgumentException>(() => IdentityHubDirectory.Create(_store, sameId));
        ArgumentException name = Should.Throw<ArgumentException>(() => IdentityHubDirectory.Create(_store, sameName));

        // Assert
        id.ParamName.ShouldBe("descriptor");
        id.Message.ShouldContain("Group 'all-staff", Case.Sensitive);
        id.Message.ShouldContain("defined twice");
        name.ParamName.ShouldBe("descriptor");
        name.Message.ShouldContain("Group name 'everyone'", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Create: Should accept a user and a group that share a name")]
    public async Task Create_WithAUserAndAGroupOfOneName_ShouldServeBoth()
    {
        // Arrange: names are unique within a kind, not across kinds.
        var descriptor = new IdentityHubDirectoryDescriptor();
        descriptor.Users.Add(TestDirectory.User("operations"));
        descriptor.Groups.Add(TestDirectory.Group("operations"));

        // Act
        IdentityHubDirectory directory = IdentityHubDirectory.Create(_store, descriptor);
        IdentityObjectWriteResult storedUser = await directory.Users.TryAddAsync(TestDirectory.User("support"), CancellationToken.None);
        IdentityObjectWriteResult storedGroup = await directory.Groups.TryAddAsync(TestDirectory.Group("support"), CancellationToken.None);

        // Assert
        directory.Users.Locked.Count.ShouldBe(1);
        directory.Groups.Locked.Count.ShouldBe(1);
        storedUser.Status.ShouldBe(IdentityObjectWriteStatus.Written);
        storedGroup.Status.ShouldBe(IdentityObjectWriteStatus.Written);
        IdentityUserId.FromName("operations").Value.ShouldNotBe(IdentityGroupId.FromName("operations").Value);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Create: Should serve what another directory stored in the same store")]
    public async Task Create_OverAStoreAnotherDirectoryWroteTo_ShouldServeItsStoredObjects()
    {
        // Arrange: two nodes that run the same code over one store.
        IdentityHubDirectory first = IdentityHubDirectory.Create(_store);
        IdentityHubDirectory second = IdentityHubDirectory.Create(_store);

        // Act
        await first.Users.TryAddAsync(TestDirectory.User("ada"), CancellationToken.None);
        await first.Groups.TryAddAsync(TestDirectory.Group("everyone"), CancellationToken.None);
        IdentityUser? user = await second.Users.FindByNameAsync("ada", CancellationToken.None);
        IdentityGroup? group = await second.Groups.FindByNameAsync("everyone", CancellationToken.None);

        // Assert
        user.ShouldNotBeNull();
        user.IsLocked.ShouldBeFalse();
        group.ShouldNotBeNull();
        group.IsLocked.ShouldBeFalse();
    }
}
