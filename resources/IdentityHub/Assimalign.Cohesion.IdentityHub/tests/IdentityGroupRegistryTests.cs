using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.IdentityHub.Tests;

/// <summary>
/// What the groups registry adds to the set every directory object shares, which
/// <see cref="IdentityUserRegistryTests"/> covers: its own record set, its own identifiers, and
/// its own argument checks.
/// </summary>
public class IdentityGroupRegistryTests
{
    private static readonly IdentityHubRecordSetName _groups = new("groups");
    private static readonly IdentityHubRecordSetName _users = new("users");

    private readonly InMemoryIdentityHubStore _store = new();

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Locked: Should serve the groups defined in code locked, in the order of their identifiers")]
    public void Locked_WithGroupsDefinedInCode_ShouldLockAndOrderThem()
    {
        // Act
        IIdentityGroupRegistry groups = Groups(
            TestDirectory.Group("orders-admins"),
            TestDirectory.Group("billing.readers"),
            TestDirectory.Group("everyone"));

        // Assert
        groups.Locked.Select(group => group.Id.ToString()).ShouldBe(
            new[] { "orders-admins", "billing.readers", "everyone" }
                .Select(name => IdentityGroupId.FromName(name).ToString())
                .OrderBy(id => id, StringComparer.Ordinal));
        groups.Locked.ShouldAllBe(group => group.IsLocked && group.Version == null);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - FindByNameAsync: Should find a group defined in code and a stored one by name in any case")]
    public async Task FindByNameAsync_WithALockedAndAStoredGroup_ShouldFindBothWithoutRegardToCase()
    {
        // Arrange
        IIdentityGroupRegistry groups = Groups(TestDirectory.Group("Orders-Admins"));
        await groups.TryAddAsync(TestDirectory.Group("Billing.Readers"), CancellationToken.None);

        // Act
        IdentityGroup? locked = await groups.FindByNameAsync("orders-admins", CancellationToken.None);
        IdentityGroup? stored = await groups.FindByNameAsync("BILLING.READERS", CancellationToken.None);
        IdentityGroup? missing = await groups.FindByNameAsync("everyone", CancellationToken.None);

        // Assert
        locked.ShouldNotBeNull();
        locked.IsLocked.ShouldBeTrue();
        locked.Name.Value.ShouldBe("Orders-Admins");
        stored.ShouldNotBeNull();
        stored.IsLocked.ShouldBeFalse();
        stored.Id.ShouldBe(IdentityGroupId.FromName("billing.readers"));
        missing.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Writes: Should add, replace, and remove a stored group")]
    public async Task Writes_WithAStoredGroup_ShouldAddReplaceAndRemoveIt()
    {
        // Arrange
        IIdentityGroupRegistry groups = Groups();
        IdentityGroupId id = IdentityGroupId.FromName("orders-admins");

        // Act
        IdentityObjectWriteResult added = await groups.TryAddAsync(TestDirectory.Group("orders-admins"), CancellationToken.None);
        IdentityGroupDescriptor change = IdentityGroupDescriptor.From((await groups.FindAsync(id, CancellationToken.None))!);
        change.Name = "orders-owners";
        change.Description = "Everyone who may change an order.";
        IdentityObjectWriteResult replaced = await groups.TryReplaceAsync(IdentityGroup.Create(change), added.Version, CancellationToken.None);
        IdentityGroup renamed = (await groups.FindByNameAsync("orders-owners", CancellationToken.None))!;
        IdentityObjectWriteResult removed = await groups.TryRemoveAsync(id, replaced.Version, CancellationToken.None);

        // Assert
        added.Status.ShouldBe(IdentityObjectWriteStatus.Written);
        replaced.Status.ShouldBe(IdentityObjectWriteStatus.Written);
        renamed.Id.ShouldBe(id);
        renamed.Description.ShouldBe("Everyone who may change an order.");
        renamed.Version.ShouldBe(replaced.Version);
        removed.Status.ShouldBe(IdentityObjectWriteStatus.Removed);
        (await groups.FindAsync(id, CancellationToken.None)).ShouldBeNull();
        _store.CountHeld(_groups).ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Writes: Should refuse a write that names a group code defines or a name it holds")]
    public async Task Writes_WithAGroupCodeDefines_ShouldRefuse()
    {
        // Arrange
        IIdentityGroupRegistry groups = Groups(TestDirectory.Group("orders-admins"));

        // Act
        IdentityObjectWriteResult add = await groups.TryAddAsync(TestDirectory.Group("orders-admins"), CancellationToken.None);
        IdentityObjectWriteResult name = await groups.TryAddAsync(
            TestDirectory.Group("ORDERS-ADMINS", id: IdentityGroupId.New()),
            CancellationToken.None);
        IdentityObjectWriteResult replace = await groups.TryReplaceAsync(TestDirectory.Group("orders-admins"), "1", CancellationToken.None);
        IdentityObjectWriteResult remove = await groups.TryRemoveAsync(IdentityGroupId.FromName("orders-admins"), "1", CancellationToken.None);

        // Assert
        add.Status.ShouldBe(IdentityObjectWriteStatus.Locked);
        name.Status.ShouldBe(IdentityObjectWriteStatus.NameConflict);
        replace.Status.ShouldBe(IdentityObjectWriteStatus.Locked);
        remove.Status.ShouldBe(IdentityObjectWriteStatus.Locked);
        _store.CountHeld(_groups).ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Registry: Should refuse the null and default arguments of a groups registry")]
    public async Task Registry_WithNullOrDefaultArguments_ShouldThrow()
    {
        // Arrange
        IIdentityGroupRegistry groups = Groups();

        // Act
        ArgumentException findNoId = await Should.ThrowAsync<ArgumentException>(
            async () => await groups.FindAsync(default, CancellationToken.None));
        ArgumentException findNoName = await Should.ThrowAsync<ArgumentException>(
            async () => await groups.FindByNameAsync(default, CancellationToken.None));
        ArgumentNullException addNull = await Should.ThrowAsync<ArgumentNullException>(
            async () => await groups.TryAddAsync(null!, CancellationToken.None));
        ArgumentNullException replaceNull = await Should.ThrowAsync<ArgumentNullException>(
            async () => await groups.TryReplaceAsync(null!, "1", CancellationToken.None));
        ArgumentException replaceNoVersion = await Should.ThrowAsync<ArgumentException>(
            async () => await groups.TryReplaceAsync(TestDirectory.Group("everyone"), default, CancellationToken.None));
        ArgumentException removeNoId = await Should.ThrowAsync<ArgumentException>(
            async () => await groups.TryRemoveAsync(default, "1", CancellationToken.None));
        ArgumentException removeNoVersion = await Should.ThrowAsync<ArgumentException>(
            async () => await groups.TryRemoveAsync(IdentityGroupId.FromName("everyone"), default, CancellationToken.None));

        // Assert
        findNoId.ParamName.ShouldBe("id");
        findNoName.ParamName.ShouldBe("name");
        addNull.ParamName.ShouldBe("value");
        replaceNull.ParamName.ShouldBe("value");
        replaceNoVersion.ParamName.ShouldBe("expectedVersion");
        removeNoId.ParamName.ShouldBe("id");
        removeNoVersion.ParamName.ShouldBe("expectedVersion");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - ListAsync: Should list the groups defined in code before the stored ones")]
    public async Task ListAsync_WithLockedAndStoredGroups_ShouldListTheLockedOnesFirst()
    {
        // Arrange
        IIdentityGroupRegistry groups = Groups(TestDirectory.Group("orders-admins"));
        await groups.TryAddAsync(TestDirectory.Group("everyone"), CancellationToken.None);

        // Act
        IdentityObjectPage<IdentityGroup> first = await groups.ListAsync(1, cancellationToken: CancellationToken.None);
        IdentityObjectPage<IdentityGroup> second = await groups.ListAsync(1, first.Continuation, CancellationToken.None);

        // Assert
        first.Items.Single().Name.Value.ShouldBe("orders-admins");
        first.Continuation.ShouldNotBeNull();
        second.Items.Single().Name.Value.ShouldBe("everyone");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - ListAsync: Should refuse a continuation the groups registry did not issue, naming the groups record set")]
    public async Task ListAsync_WithAContinuationItDidNotIssue_ShouldNameTheGroupsSet()
    {
        // Arrange
        IIdentityGroupRegistry groups = Groups();

        // Act
        IdentityHubStoreException exception = await Should.ThrowAsync<IdentityHubStoreException>(
            async () => await groups.ListAsync(10, "x", CancellationToken.None));

        // Assert
        exception.Code.ShouldBe(IdentityHubErrorCode.StoreContinuationInvalid);
        exception.Set!.Value.Value.ShouldBe("groups");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Writes: Should keep groups in their own record set, apart from users")]
    public async Task Writes_WithAGroup_ShouldUseTheGroupsRecordSetOnly()
    {
        // Arrange
        IIdentityGroupRegistry groups = Groups();

        // Act
        await groups.TryAddAsync(TestDirectory.Group("everyone"), CancellationToken.None);

        // Assert
        _store.CountHeld(_groups).ShouldBe(1);
        _store.CountHeld(_users).ShouldBe(0);
    }

    private IIdentityGroupRegistry Groups(params IdentityGroup[] defined)
    {
        return TestDirectory.WithGroups(_store, defined).Groups;
    }
}
