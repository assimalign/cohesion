using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.IdentityHub.Tests;

/// <summary>
/// A directory serves the users defined in code locked, stores the ones created at run time,
/// and lets code win where the two meet. Every kind of directory object is served by the same
/// set, so these tests are also the tests of that set.
/// </summary>
public class IdentityUserRegistryTests
{
    // The identifier IdentityUserId.FromName derives from "ada", as a store key spells it.
    private const string adaId = "e2bffadd-19cb-5bce-817b-8a6008fd4d28";

    private static readonly IdentityHubRecordSetName _users = new("users");

    private readonly InMemoryIdentityHubStore _store = new();

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Locked: Should serve the users defined in code locked, in the order of their identifiers")]
    public void Locked_WithUsersDefinedInCode_ShouldLockAndOrderThem()
    {
        // Act
        IIdentityUserRegistry users = Users(TestDirectory.User("grace"), TestDirectory.User("ada"), TestDirectory.User("linus"));
        IIdentityUserRegistry reordered = Users(TestDirectory.User("linus"), TestDirectory.User("grace"), TestDirectory.User("ada"));

        // Assert
        IdentityUserId[] expected = InIdentifierOrder("ada", "grace", "linus");
        users.Locked.Select(user => user.Id).ShouldBe(expected);
        reordered.Locked.Select(user => user.Id).ShouldBe(expected);
        users.Locked.ShouldAllBe(user => user.IsLocked && user.Version == null);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Locked: Should never write a user defined in code to the store")]
    public async Task Locked_WithUsersDefinedInCode_ShouldLeaveTheStoreUntouched()
    {
        // Arrange
        var recording = new RecordingIdentityHubStore();
        IIdentityUserRegistry users = TestDirectory.WithUsers(recording, TestDirectory.User("ada"), TestDirectory.User("grace")).Users;

        // Act
        IdentityUser? byId = await users.FindAsync(IdentityUserId.FromName("ada"), CancellationToken.None);
        IdentityUser? byName = await users.FindByNameAsync("ADA", CancellationToken.None);

        // Assert
        byId.ShouldNotBeNull();
        byName.ShouldBeSameAs(byId);
        recording.Calls.ShouldBeEmpty();
        Users(TestDirectory.User("ada"));
        _store.CountHeld(_users).ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - FindAsync: Should return a stored user with its version, unlocked")]
    public async Task FindAsync_WithAStoredUser_ShouldReturnItWithItsVersion()
    {
        // Arrange
        IIdentityUserRegistry users = Users();
        IdentityObjectWriteResult added = await users.TryAddAsync(TestDirectory.User("ada", "Ada Lovelace"), CancellationToken.None);

        // Act
        IdentityUser? found = await users.FindAsync(IdentityUserId.FromName("ada"), CancellationToken.None);
        IdentityUser? missing = await users.FindAsync(IdentityUserId.FromName("grace"), CancellationToken.None);

        // Assert
        found.ShouldNotBeNull();
        found.DisplayName.ShouldBe("Ada Lovelace");
        found.IsLocked.ShouldBeFalse();
        found.Version.ShouldBe(added.Version);
        missing.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - FindByNameAsync: Should find a stored user by its name in any case")]
    public async Task FindByNameAsync_WithAStoredUser_ShouldFindItWithoutRegardToCase()
    {
        // Arrange
        IIdentityUserRegistry users = Users();
        await users.TryAddAsync(TestDirectory.User("Ada@Contoso.example"), CancellationToken.None);

        // Act
        IdentityUser? exact = await users.FindByNameAsync("Ada@Contoso.example", CancellationToken.None);
        IdentityUser? lower = await users.FindByNameAsync("ada@contoso.example", CancellationToken.None);
        IdentityUser? upper = await users.FindByNameAsync("ADA@CONTOSO.EXAMPLE", CancellationToken.None);
        IdentityUser? missing = await users.FindByNameAsync("grace@contoso.example", CancellationToken.None);

        // Assert
        exact.ShouldNotBeNull();
        exact.Name.Value.ShouldBe("Ada@Contoso.example");
        lower!.Id.ShouldBe(exact.Id);
        upper!.Id.ShouldBe(exact.Id);
        missing.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - FindAsync, FindByNameAsync: Should refuse a default argument and observe a canceled token")]
    public async Task Find_WithADefaultArgumentOrACanceledToken_ShouldThrow()
    {
        // Arrange
        IIdentityUserRegistry users = Users(TestDirectory.User("ada"));
        using var source = new CancellationTokenSource();
        await source.CancelAsync();

        // Act
        ArgumentException noId = await Should.ThrowAsync<ArgumentException>(
            async () => await users.FindAsync(default, CancellationToken.None));
        ArgumentException noName = await Should.ThrowAsync<ArgumentException>(
            async () => await users.FindByNameAsync(default, CancellationToken.None));

        // Assert
        noId.ParamName.ShouldBe("id");
        noName.ParamName.ShouldBe("name");
        await Should.ThrowAsync<OperationCanceledException>(
            async () => await users.FindAsync(IdentityUserId.FromName("ada"), source.Token));
        await Should.ThrowAsync<OperationCanceledException>(
            async () => await users.FindByNameAsync("ada", source.Token));
        await Should.ThrowAsync<OperationCanceledException>(
            async () => await users.ListAsync(cancellationToken: source.Token));
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - FindAsync: Should report a stored record that is no user as corrupt")]
    public async Task FindAsync_WithAStoredRecordThatIsNotAUser_ShouldThrowRecordCorrupt()
    {
        // Arrange
        await _store.TryAddAsync(
            _users,
            new IdentityHubRecord(new IdentityHubRecordKey(adaId), Encoding.UTF8.GetBytes("""{"v":1,"name":"ada"}""")),
            CancellationToken.None);
        IIdentityUserRegistry users = Users();

        // Act
        IdentityHubStoreException exception = await Should.ThrowAsync<IdentityHubStoreException>(
            async () => await users.FindAsync(IdentityUserId.FromName("ada"), CancellationToken.None));

        // Assert
        exception.Code.ShouldBe(IdentityHubErrorCode.StoreRecordCorrupt);
        exception.Set!.Value.Value.ShouldBe("users");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - TryAddAsync: Should store a new user")]
    public async Task TryAddAsync_WithANewUser_ShouldStoreIt()
    {
        // Arrange
        IIdentityUserRegistry users = Users();

        // Act
        IdentityObjectWriteResult result = await users.TryAddAsync(TestDirectory.User("ada"), CancellationToken.None);

        // Assert
        result.Status.ShouldBe(IdentityObjectWriteStatus.Written);
        result.Version.Value.ShouldNotBeNull();
        result.IsApplied.ShouldBeTrue();
        _store.CountHeld(_users).ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - TryAddAsync: Should answer Locked for an identifier code defines")]
    public async Task TryAddAsync_WithAnIdentifierCodeDefines_ShouldAnswerLocked()
    {
        // Arrange
        IIdentityUserRegistry users = Users(TestDirectory.User("ada"));

        // Act
        IdentityObjectWriteResult result = await users.TryAddAsync(
            TestDirectory.User("ada.lovelace", id: IdentityUserId.FromName("ada")),
            CancellationToken.None);

        // Assert
        result.Status.ShouldBe(IdentityObjectWriteStatus.Locked);
        _store.CountHeld(_users).ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - TryAddAsync: Should answer IdConflict for an identifier a stored user has")]
    public async Task TryAddAsync_WithAStoredIdentifier_ShouldAnswerIdConflict()
    {
        // Arrange
        IIdentityUserRegistry users = Users();
        await users.TryAddAsync(TestDirectory.User("ada"), CancellationToken.None);

        // Act
        IdentityObjectWriteResult result = await users.TryAddAsync(
            TestDirectory.User("ada.lovelace", id: IdentityUserId.FromName("ada")),
            CancellationToken.None);

        // Assert
        result.Status.ShouldBe(IdentityObjectWriteStatus.IdConflict);
        _store.CountHeld(_users).ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - TryAddAsync: Should answer NameConflict for a name code holds, in any case")]
    public async Task TryAddAsync_WithANameCodeHolds_ShouldAnswerNameConflict()
    {
        // Arrange
        IIdentityUserRegistry users = Users(TestDirectory.User("ada"));

        // Act
        IdentityObjectWriteResult result = await users.TryAddAsync(
            TestDirectory.User("ADA", id: IdentityUserId.New()),
            CancellationToken.None);

        // Assert
        result.Status.ShouldBe(IdentityObjectWriteStatus.NameConflict);
        _store.CountHeld(_users).ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - TryAddAsync: Should answer NameConflict for a name a stored user holds, in any case")]
    public async Task TryAddAsync_WithANameAStoredUserHolds_ShouldAnswerNameConflict()
    {
        // Arrange
        IIdentityUserRegistry users = Users();
        await users.TryAddAsync(TestDirectory.User("ada"), CancellationToken.None);

        // Act
        IdentityObjectWriteResult result = await users.TryAddAsync(
            TestDirectory.User("Ada", id: IdentityUserId.New()),
            CancellationToken.None);

        // Assert
        result.Status.ShouldBe(IdentityObjectWriteStatus.NameConflict);
        _store.CountHeld(_users).ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - TryAddAsync: Should store the contents of a user another directory served")]
    public async Task TryAddAsync_WithAUserServedByAnotherDirectory_ShouldStoreItsContents()
    {
        // Arrange
        IdentityUser locked = TestDirectory.WithUsers(new InMemoryIdentityHubStore(), TestDirectory.User("ada", "Ada Lovelace")).Users.Locked[0];
        IIdentityUserRegistry users = Users();

        // Act
        IdentityObjectWriteResult result = await users.TryAddAsync(locked, CancellationToken.None);
        IdentityUser stored = (await users.FindAsync(locked.Id, CancellationToken.None))!;

        // Assert
        result.Status.ShouldBe(IdentityObjectWriteStatus.Written);
        stored.IsLocked.ShouldBeFalse();
        stored.DisplayName.ShouldBe("Ada Lovelace");
        stored.Version.ShouldBe(result.Version);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - TryReplaceAsync: Should replace a stored user at the expected version")]
    public async Task TryReplaceAsync_WithTheExpectedVersion_ShouldReplaceTheUser()
    {
        // Arrange
        IIdentityUserRegistry users = Users();
        IdentityObjectWriteResult added = await users.TryAddAsync(TestDirectory.User("ada"), CancellationToken.None);
        IdentityUser read = (await users.FindAsync(IdentityUserId.FromName("ada"), CancellationToken.None))!;
        IdentityUserDescriptor change = IdentityUserDescriptor.From(read);
        change.DisplayName = "Ada Lovelace";
        change.IsEnabled = false;

        // Act
        IdentityObjectWriteResult replaced = await users.TryReplaceAsync(IdentityUser.Create(change), read.Version!.Value, CancellationToken.None);
        IdentityUser after = (await users.FindAsync(read.Id, CancellationToken.None))!;

        // Assert
        replaced.Status.ShouldBe(IdentityObjectWriteStatus.Written);
        replaced.Version.ShouldNotBe(added.Version);
        after.DisplayName.ShouldBe("Ada Lovelace");
        after.IsEnabled.ShouldBeFalse();
        after.Version.ShouldBe(replaced.Version);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - TryReplaceAsync: Should rename a stored user, keep its identifier, and free the old name")]
    public async Task TryReplaceAsync_WithANewName_ShouldKeepTheIdentifierAndFreeTheOldName()
    {
        // Arrange
        IIdentityUserRegistry users = Users();
        IdentityObjectWriteResult added = await users.TryAddAsync(TestDirectory.User("ada"), CancellationToken.None);
        IdentityUserDescriptor change = IdentityUserDescriptor.From(TestDirectory.User("ada"));
        change.Name = "ada.lovelace";

        // Act
        IdentityObjectWriteResult renamed = await users.TryReplaceAsync(IdentityUser.Create(change), added.Version, CancellationToken.None);
        IdentityUser? byOldName = await users.FindByNameAsync("ada", CancellationToken.None);
        IdentityUser? byNewName = await users.FindByNameAsync("ada.lovelace", CancellationToken.None);
        IdentityObjectWriteResult reuse = await users.TryAddAsync(TestDirectory.User("ada", id: IdentityUserId.New()), CancellationToken.None);

        // Assert
        renamed.Status.ShouldBe(IdentityObjectWriteStatus.Written);
        byOldName.ShouldBeNull();
        byNewName!.Id.ShouldBe(IdentityUserId.FromName("ada"));
        reuse.Status.ShouldBe(IdentityObjectWriteStatus.Written);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - TryReplaceAsync: Should answer VersionConflict for a stale version and for no stored user")]
    public async Task TryReplaceAsync_WithAStaleVersionOrNoUser_ShouldAnswerVersionConflict()
    {
        // Arrange
        IIdentityUserRegistry users = Users();
        IdentityObjectWriteResult added = await users.TryAddAsync(TestDirectory.User("ada"), CancellationToken.None);
        await users.TryReplaceAsync(TestDirectory.User("ada", "Ada"), added.Version, CancellationToken.None);

        // Act
        IdentityObjectWriteResult stale = await users.TryReplaceAsync(TestDirectory.User("ada", "Late"), added.Version, CancellationToken.None);
        IdentityObjectWriteResult absent = await users.TryReplaceAsync(TestDirectory.User("grace"), added.Version, CancellationToken.None);

        // Assert
        stale.Status.ShouldBe(IdentityObjectWriteStatus.VersionConflict);
        absent.Status.ShouldBe(IdentityObjectWriteStatus.VersionConflict);
        (await users.FindAsync(IdentityUserId.FromName("ada"), CancellationToken.None))!.DisplayName.ShouldBe("Ada");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - TryReplaceAsync: Should answer Locked for a user code defines and NameConflict for a name another user holds")]
    public async Task TryReplaceAsync_WithALockedUserOrATakenName_ShouldRefuse()
    {
        // Arrange
        IIdentityUserRegistry users = Users(TestDirectory.User("ada"));
        IdentityObjectWriteResult grace = await users.TryAddAsync(TestDirectory.User("grace"), CancellationToken.None);
        await users.TryAddAsync(TestDirectory.User("linus"), CancellationToken.None);

        // Act
        IdentityObjectWriteResult locked = await users.TryReplaceAsync(TestDirectory.User("ada", "Changed"), "1", CancellationToken.None);
        IdentityObjectWriteResult lockedName = await users.TryReplaceAsync(
            TestDirectory.User("Ada", id: IdentityUserId.FromName("grace")),
            grace.Version,
            CancellationToken.None);
        IdentityObjectWriteResult storedName = await users.TryReplaceAsync(
            TestDirectory.User("LINUS", id: IdentityUserId.FromName("grace")),
            grace.Version,
            CancellationToken.None);

        // Assert
        locked.Status.ShouldBe(IdentityObjectWriteStatus.Locked);
        lockedName.Status.ShouldBe(IdentityObjectWriteStatus.NameConflict);
        storedName.Status.ShouldBe(IdentityObjectWriteStatus.NameConflict);
        (await users.FindByNameAsync("grace", CancellationToken.None))!.Version.ShouldBe(grace.Version);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - TryRemoveAsync: Should remove a stored user at the expected version and free its name")]
    public async Task TryRemoveAsync_WithTheExpectedVersion_ShouldRemoveTheUser()
    {
        // Arrange
        IIdentityUserRegistry users = Users();
        IdentityObjectWriteResult added = await users.TryAddAsync(TestDirectory.User("ada"), CancellationToken.None);

        // Act
        IdentityObjectWriteResult stale = await users.TryRemoveAsync(IdentityUserId.FromName("ada"), "not-the-version", CancellationToken.None);
        IdentityObjectWriteResult removed = await users.TryRemoveAsync(IdentityUserId.FromName("ada"), added.Version, CancellationToken.None);
        IdentityObjectWriteResult again = await users.TryRemoveAsync(IdentityUserId.FromName("ada"), added.Version, CancellationToken.None);

        // Assert
        stale.Status.ShouldBe(IdentityObjectWriteStatus.VersionConflict);
        removed.Status.ShouldBe(IdentityObjectWriteStatus.Removed);
        removed.IsApplied.ShouldBeTrue();
        again.Status.ShouldBe(IdentityObjectWriteStatus.VersionConflict);
        (await users.FindByNameAsync("ada", CancellationToken.None)).ShouldBeNull();
        _store.CountHeld(_users).ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - TryRemoveAsync: Should answer Locked for a user code defines")]
    public async Task TryRemoveAsync_WithAUserCodeDefines_ShouldAnswerLocked()
    {
        // Arrange
        IIdentityUserRegistry users = Users(TestDirectory.User("ada"));

        // Act
        IdentityObjectWriteResult result = await users.TryRemoveAsync(IdentityUserId.FromName("ada"), "1", CancellationToken.None);

        // Assert
        result.Status.ShouldBe(IdentityObjectWriteStatus.Locked);
        users.Locked.Count.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Writes: Should refuse null and default arguments")]
    public async Task Writes_WithNullOrDefaultArguments_ShouldThrow()
    {
        // Arrange
        IIdentityUserRegistry users = Users();

        // Act
        ArgumentNullException addNull = await Should.ThrowAsync<ArgumentNullException>(
            async () => await users.TryAddAsync(null!, CancellationToken.None));
        ArgumentNullException replaceNull = await Should.ThrowAsync<ArgumentNullException>(
            async () => await users.TryReplaceAsync(null!, "1", CancellationToken.None));
        ArgumentException replaceNoVersion = await Should.ThrowAsync<ArgumentException>(
            async () => await users.TryReplaceAsync(TestDirectory.User("ada"), default, CancellationToken.None));
        ArgumentException removeNoId = await Should.ThrowAsync<ArgumentException>(
            async () => await users.TryRemoveAsync(default, "1", CancellationToken.None));
        ArgumentException removeNoVersion = await Should.ThrowAsync<ArgumentException>(
            async () => await users.TryRemoveAsync(IdentityUserId.FromName("ada"), default, CancellationToken.None));

        // Assert
        addNull.ParamName.ShouldBe("value");
        replaceNull.ParamName.ShouldBe("value");
        replaceNoVersion.ParamName.ShouldBe("expectedVersion");
        removeNoId.ParamName.ShouldBe("id");
        removeNoVersion.ParamName.ShouldBe("expectedVersion");
        _store.CountHeld(_users).ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Shadowing: Should serve the user code defines over a stored one with its identifier")]
    public async Task Shadowing_WithAStoredUserCodeNowDefines_ShouldServeOnlyTheLockedOne()
    {
        // Arrange: a user created at run time and renamed, then a deployment that defines it.
        IIdentityUserRegistry before = Users();
        IdentityObjectWriteResult added = await before.TryAddAsync(TestDirectory.User("ada"), CancellationToken.None);
        IdentityObjectWriteResult renamed = await before.TryReplaceAsync(
            TestDirectory.User("ada.runtime", "Created at run time", IdentityUserId.FromName("ada")),
            added.Version,
            CancellationToken.None);
        IIdentityUserRegistry after = Users(TestDirectory.User("ada", "Ada Lovelace"));

        // Act
        IdentityUser found = (await after.FindAsync(IdentityUserId.FromName("ada"), CancellationToken.None))!;
        IdentityUser? byShadowedName = await after.FindByNameAsync("ada.runtime", CancellationToken.None);
        IdentityObjectPage<IdentityUser> page = await after.ListAsync(cancellationToken: CancellationToken.None);
        IdentityObjectWriteResult remove = await after.TryRemoveAsync(IdentityUserId.FromName("ada"), renamed.Version, CancellationToken.None);
        IdentityObjectWriteResult claim = await after.TryAddAsync(
            TestDirectory.User("ada.runtime", id: IdentityUserId.New()),
            CancellationToken.None);

        // Assert: the shadowed record stays in the store, and its name stays taken among stored users.
        found.IsLocked.ShouldBeTrue();
        found.DisplayName.ShouldBe("Ada Lovelace");
        byShadowedName.ShouldBeNull();
        page.Items.Single().ShouldBeSameAs(found);
        remove.Status.ShouldBe(IdentityObjectWriteStatus.Locked);
        claim.Status.ShouldBe(IdentityObjectWriteStatus.NameConflict);
        _store.CountHeld(_users).ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Shadowing: Should serve the stored user again once code no longer defines it")]
    public async Task Shadowing_WhenCodeNoLongerDefinesTheUser_ShouldServeTheStoredOneAgain()
    {
        // Arrange
        IIdentityUserRegistry before = Users();
        await before.TryAddAsync(TestDirectory.User("ada", "Created at run time"), CancellationToken.None);
        Users(TestDirectory.User("ada", "Ada Lovelace"));

        // Act
        IIdentityUserRegistry rolledBack = Users();
        IdentityUser found = (await rolledBack.FindAsync(IdentityUserId.FromName("ada"), CancellationToken.None))!;

        // Assert
        found.IsLocked.ShouldBeFalse();
        found.DisplayName.ShouldBe("Created at run time");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Adoption: Should lock a user created at run time once code defines its name")]
    public async Task Adoption_WhenCodeDefinesTheNameAUserWasCreatedUnder_ShouldServeTheCodeDefinitionLocked()
    {
        // Arrange: a user created at run time under a name, then a deployment whose code defines
        // that name in another case.
        IIdentityUserRegistry before = Users();
        IdentityObjectWriteResult created = await before.TryAddAsync(
            TestDirectory.User("Ada@Contoso.example", "Created at run time"),
            CancellationToken.None);
        IdentityUser defined = TestDirectory.User("ada@contoso.example", "Ada Lovelace");

        // Act
        IIdentityUserRegistry after = Users(defined);
        IdentityUser served = (await after.FindByNameAsync("ADA@CONTOSO.EXAMPLE", CancellationToken.None))!;
        IdentityObjectWriteResult replace = await after.TryReplaceAsync(defined, created.Version, CancellationToken.None);
        IdentityObjectWriteResult remove = await after.TryRemoveAsync(defined.Id, created.Version, CancellationToken.None);
        IdentityObjectWriteResult add = await after.TryAddAsync(defined, CancellationToken.None);

        // Assert: code wins whole.
        created.Status.ShouldBe(IdentityObjectWriteStatus.Written);
        served.IsLocked.ShouldBeTrue();
        served.Version.ShouldBeNull();
        served.DisplayName.ShouldBe("Ada Lovelace");
        replace.Status.ShouldBe(IdentityObjectWriteStatus.Locked);
        remove.Status.ShouldBe(IdentityObjectWriteStatus.Locked);
        add.Status.ShouldBe(IdentityObjectWriteStatus.Locked);
        _store.CountHeld(_users).ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Adoption: Should leave a user created under a random identifier to the API")]
    public async Task Adoption_WithAUserCreatedUnderARandomIdentifier_ShouldNotLockIt()
    {
        // Arrange
        IIdentityUserRegistry before = Users();
        IdentityUserId random = IdentityUserId.New();
        await before.TryAddAsync(TestDirectory.User("grace", id: random), CancellationToken.None);

        // Act
        IIdentityUserRegistry after = Users(TestDirectory.User("ada"));
        IdentityUser stored = (await after.FindAsync(random, CancellationToken.None))!;
        IdentityObjectPage<IdentityUser> page = await after.ListAsync(cancellationToken: CancellationToken.None);

        // Assert
        stored.IsLocked.ShouldBeFalse();
        stored.Version.ShouldNotBeNull();
        page.Items.Select(user => user.Id).ShouldBe([IdentityUserId.FromName("ada"), random]);
        page.Items.Select(user => user.IsLocked).ShouldBe([true, false]);
    }

    [Theory(DisplayName = "Cohesion Test [IdentityHub] - ListAsync: Should return every user exactly once, whatever the page size")]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(7)]
    [InlineData(100)]
    public async Task ListAsync_PagingWithALimit_ShouldReturnEveryUserOnce(int limit)
    {
        // Arrange: three defined in code, four stored, one of which code shadows.
        IIdentityUserRegistry seed = Users();
        await seed.TryAddAsync(TestDirectory.User("stored-1"), CancellationToken.None);
        await seed.TryAddAsync(TestDirectory.User("stored-2"), CancellationToken.None);
        await seed.TryAddAsync(TestDirectory.User("locked-2", "Shadowed"), CancellationToken.None);
        await seed.TryAddAsync(TestDirectory.User("stored-3"), CancellationToken.None);
        IIdentityUserRegistry users = Users(
            TestDirectory.User("locked-3"),
            TestDirectory.User("locked-1"),
            TestDirectory.User("locked-2"));

        // Act
        List<IdentityUser> listed = await TestDirectory.ListAllAsync(users, limit);

        // Assert
        listed.Select(user => user.Id).Take(3).ShouldBe(InIdentifierOrder("locked-1", "locked-2", "locked-3"));
        listed.Select(user => user.Id).Skip(3).ShouldBe(Ids("stored-1", "stored-2", "stored-3"), ignoreOrder: true);
        listed.Count(user => user.IsLocked).ShouldBe(3);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - ListAsync: Should continue into the stored users after a page the locked ones fill")]
    public async Task ListAsync_WhenTheLockedUsersFillAPage_ShouldContinueIntoTheStore()
    {
        // Arrange
        IIdentityUserRegistry users = Users(TestDirectory.User("ada"), TestDirectory.User("grace"));
        await users.TryAddAsync(TestDirectory.User("linus"), CancellationToken.None);
        IdentityUserId[] locked = InIdentifierOrder("ada", "grace");

        // Act
        IdentityObjectPage<IdentityUser> first = await users.ListAsync(1, cancellationToken: CancellationToken.None);
        IdentityObjectPage<IdentityUser> second = await users.ListAsync(1, first.Continuation, CancellationToken.None);
        IdentityObjectPage<IdentityUser> third = await users.ListAsync(1, second.Continuation, CancellationToken.None);

        // Assert
        first.Items.Single().Id.ShouldBe(locked[0]);
        first.Continuation.ShouldNotBeNull();
        second.Items.Single().Id.ShouldBe(locked[1]);
        second.Continuation.ShouldNotBeNull();
        third.Items.Single().Id.ShouldBe(IdentityUserId.FromName("linus"));
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - ListAsync: Should list an empty directory's users as one empty last page")]
    public async Task ListAsync_WithNoUsers_ShouldReturnAnEmptyLastPage()
    {
        // Arrange
        IIdentityUserRegistry users = Users();

        // Act
        IdentityObjectPage<IdentityUser> page = await users.ListAsync(cancellationToken: CancellationToken.None);

        // Assert
        page.Items.ShouldBeEmpty();
        page.Continuation.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - ListAsync: Should follow a store that returns short and empty pages of users")]
    public async Task ListAsync_OverAStoreThatPagesShort_ShouldStillReturnEveryUser()
    {
        // Arrange
        IIdentityUserRegistry seed = Users();
        await seed.TryAddAsync(TestDirectory.User("stored-1"), CancellationToken.None);
        await seed.TryAddAsync(TestDirectory.User("stored-2"), CancellationToken.None);
        await seed.TryAddAsync(TestDirectory.User("stored-3"), CancellationToken.None);
        IIdentityUserRegistry users = TestDirectory.WithUsers(new ShortPageIdentityHubStore(_store), TestDirectory.User("locked-1")).Users;

        // Act
        List<IdentityUser> listed = await TestDirectory.ListAllAsync(users, 10);
        IdentityUser? byName = await users.FindByNameAsync("stored-2", CancellationToken.None);

        // Assert
        listed.Select(user => user.Id).ShouldBe(Ids("locked-1", "stored-1", "stored-2", "stored-3"), ignoreOrder: true);
        byName!.Id.ShouldBe(IdentityUserId.FromName("stored-2"));
    }

    [Theory(DisplayName = "Cohesion Test [IdentityHub] - ListAsync: Should refuse a continuation the users registry did not issue")]
    [InlineData("x")]
    [InlineData("c")]
    [InlineData("c0")]
    [InlineData("c2")]
    [InlineData("c99")]
    [InlineData("c-1")]
    [InlineData("c1x")]
    [InlineData("snot-a-store-token")]
    public async Task ListAsync_WithAContinuationItDidNotIssue_ShouldThrowContinuationInvalid(string continuation)
    {
        // Arrange
        IIdentityUserRegistry users = Users(TestDirectory.User("ada"), TestDirectory.User("grace"));

        // Act
        IdentityHubStoreException exception = await Should.ThrowAsync<IdentityHubStoreException>(
            async () => await users.ListAsync(10, continuation, CancellationToken.None));

        // Assert
        exception.Code.ShouldBe(IdentityHubErrorCode.StoreContinuationInvalid);
        exception.Set!.Value.Value.ShouldBe("users");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - ListAsync: Should refuse a limit out of range and an empty continuation for users")]
    public async Task ListAsync_WithAnInvalidArgument_ShouldThrow()
    {
        // Arrange
        IIdentityUserRegistry users = Users();

        // Act
        ArgumentOutOfRangeException zero = await Should.ThrowAsync<ArgumentOutOfRangeException>(
            async () => await users.ListAsync(0));
        ArgumentOutOfRangeException tooMany = await Should.ThrowAsync<ArgumentOutOfRangeException>(
            async () => await users.ListAsync(IdentityHubRecordQuery.MaxLimit + 1));
        ArgumentException empty = await Should.ThrowAsync<ArgumentException>(
            async () => await users.ListAsync(10, string.Empty));

        // Assert
        zero.ParamName.ShouldBe("limit");
        tooMany.ParamName.ShouldBe("limit");
        empty.ParamName.ShouldBe("continuation");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Writes: Should report a status the store cannot answer a user write with as a store failure")]
    public async Task Writes_WhenTheStoreAnswersWithAnImpossibleStatus_ShouldThrowIdentityHubStoreException()
    {
        // Arrange
        var recording = new RecordingIdentityHubStore { WriteResult = IdentityHubWriteResult.VersionConflict };
        IIdentityUserRegistry users = IdentityHubDirectory.Create(recording).Users;

        // Act
        IdentityHubStoreException add = await Should.ThrowAsync<IdentityHubStoreException>(
            async () => await users.TryAddAsync(TestDirectory.User("ada"), CancellationToken.None));
        recording.WriteResult = IdentityHubWriteResult.KeyConflict;
        IdentityHubStoreException replace = await Should.ThrowAsync<IdentityHubStoreException>(
            async () => await users.TryReplaceAsync(TestDirectory.User("ada"), "1", CancellationToken.None));

        // Assert
        add.Code.ShouldBe(IdentityHubErrorCode.Unknown);
        add.Set!.Value.Value.ShouldBe("users");
        replace.Code.ShouldBe(IdentityHubErrorCode.Unknown);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Writes: Should pass the caller's cancellation token and the user's record to the store")]
    public async Task Writes_WithACancellationToken_ShouldPassItToTheStore()
    {
        // Arrange
        var recording = new RecordingIdentityHubStore();
        IIdentityUserRegistry users = IdentityHubDirectory.Create(recording).Users;
        using var source = new CancellationTokenSource();

        // Act
        await users.TryAddAsync(TestDirectory.User("ada"), source.Token);

        // Assert
        recording.Calls.ShouldBe(["TryAddCoreAsync"]);
        recording.Set.Value.ShouldBe("users");
        recording.Record!.Key.Value.ShouldBe(adaId);
        recording.CancellationToken.ShouldBe(source.Token);
    }

    private IIdentityUserRegistry Users(params IdentityUser[] defined)
    {
        return TestDirectory.WithUsers(_store, defined).Users;
    }

    private static IdentityUserId[] Ids(params string[] names)
    {
        return names.Select(name => IdentityUserId.FromName(name)).ToArray();
    }

    // The order a registry documents for its locked objects: the ordinal order of the
    // identifiers' text. Sorting the text here keeps the expectation apart from Guid.CompareTo.
    private static IdentityUserId[] InIdentifierOrder(params string[] names)
    {
        return Ids(names).OrderBy(id => id.ToString(), StringComparer.Ordinal).ToArray();
    }
}
