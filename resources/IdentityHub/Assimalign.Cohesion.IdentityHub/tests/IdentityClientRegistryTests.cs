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
/// The registry serves registrations defined in code locked, stores the ones created at run
/// time, and lets code win where the two meet.
/// </summary>
public class IdentityClientRegistryTests
{
    private static readonly IdentityHubRecordSetName _clients = new("clients");

    private readonly InMemoryIdentityHubStore _store = new();

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Create: Should refuse a null store")]
    public void Create_WithANullStore_ShouldThrowArgumentNullException()
    {
        // Act
        ArgumentNullException exception = Should.Throw<ArgumentNullException>(() => IdentityClientRegistry.Create(null!));

        // Assert
        exception.ParamName.ShouldBe("store");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Create: Should serve the registrations defined in code locked, in the order of their identifiers")]
    public void Create_WithRegistrationsDefinedInCode_ShouldLockAndOrderThem()
    {
        // Act
        IdentityClientRegistry registry = IdentityClientRegistry.Create(
            _store,
            [TestClients.Web(), TestClients.Daemon(), TestClients.Api()]);

        // Assert
        registry.Locked.Select(client => client.Id.Value).ShouldBe(["orders-api", "orders-daemon", "orders-web"]);
        registry.Locked.ShouldAllBe(client => client.IsLocked && client.Version == null);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Create: Should never write a registration defined in code to the store")]
    public async Task Create_WithRegistrationsDefinedInCode_ShouldLeaveTheStoreEmpty()
    {
        // Arrange
        var recording = new RecordingIdentityHubStore();

        // Act
        IdentityClientRegistry registry = IdentityClientRegistry.Create(recording, [TestClients.Api(), TestClients.Web()]);
        IIdentityClient? found = await registry.FindAsync("orders-api", CancellationToken.None);
        IIdentityClient? byAudience = await registry.FindByAudienceAsync("api://orders", CancellationToken.None);

        // Assert
        found.ShouldNotBeNull();
        byAudience.ShouldBeSameAs(found);
        recording.Calls.ShouldBeEmpty();
        IdentityClientRegistry.Create(_store, [TestClients.Api()]);
        _store.CountHeld(_clients).ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Create: Should accept no registrations defined in code")]
    public void Create_WithNoRegistrations_ShouldHoldNoneLocked()
    {
        // Act
        IdentityClientRegistry none = IdentityClientRegistry.Create(_store);
        IdentityClientRegistry empty = IdentityClientRegistry.Create(_store, []);

        // Assert
        none.Locked.ShouldBeEmpty();
        empty.Locked.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Create: Should refuse a null registration and the same identifier twice")]
    public void Create_WithANullOrRepeatedRegistration_ShouldThrowArgumentException()
    {
        // Act
        ArgumentException nullEntry = Should.Throw<ArgumentException>(
            () => IdentityClientRegistry.Create(_store, [TestClients.Api(), null!]));
        ArgumentException repeated = Should.Throw<ArgumentException>(
            () => IdentityClientRegistry.Create(_store, [TestClients.Api(), TestClients.Api(audience: "api://other")]));

        // Assert
        nullEntry.ParamName.ShouldBe("clients");
        repeated.ParamName.ShouldBe("clients");
        repeated.Message.ShouldContain("'orders-api'", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Create: Should refuse one audience exposed by two registrations")]
    public void Create_WithOneAudienceExposedTwice_ShouldThrowArgumentExceptionNamingBoth()
    {
        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(
            () => IdentityClientRegistry.Create(_store, [TestClients.Api("orders-api"), TestClients.Api("orders-api-v2")]));

        // Assert
        exception.ParamName.ShouldBe("clients");
        exception.Message.ShouldContain("'api://orders'", Case.Sensitive);
        exception.Message.ShouldContain("'orders-api'", Case.Sensitive);
        exception.Message.ShouldContain("'orders-api-v2'", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Create: Should refuse a permission that another registration defined in code does not expose")]
    public void Create_WithAPermissionTheResourceDoesNotExpose_ShouldThrowArgumentException()
    {
        // Arrange
        IdentityClient web = IdentityClient.Create(new()
        {
            Id = "orders-web",
            Permissions = { IdentityClientPermission.Delegated("orders-api", "orders.write") },
        });
        IdentityClient daemon = IdentityClient.Create(new()
        {
            Id = "orders-daemon",
            Permissions = { IdentityClientPermission.Application("orders-api", "orders.read") },
        });

        // Act
        ArgumentException scope = Should.Throw<ArgumentException>(
            () => IdentityClientRegistry.Create(_store, [TestClients.Api(), web]));
        ArgumentException role = Should.Throw<ArgumentException>(
            () => IdentityClientRegistry.Create(_store, [TestClients.Api(), daemon]));

        // Assert
        scope.ParamName.ShouldBe("clients");
        scope.Message.ShouldContain("'orders-web'", Case.Sensitive);
        scope.Message.ShouldContain("'orders.write'", Case.Sensitive);
        role.Message.ShouldContain("'orders-daemon'", Case.Sensitive);
        role.Message.ShouldContain("'orders.read'", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Create: Should leave a permission on a registration that code does not define unresolved")]
    public void Create_WithAPermissionOnARegistrationCodeDoesNotDefine_ShouldCreate()
    {
        // Act
        IdentityClientRegistry registry = IdentityClientRegistry.Create(_store, [TestClients.Web(api: "stored-api")]);

        // Assert
        registry.Locked.Single().Permissions.Single().Resource.Value.ShouldBe("stored-api");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Create: Should copy another implementation through validation")]
    public void Create_WithAnotherImplementation_ShouldValidateAndCopyIt()
    {
        // Arrange
        var foreign = new StubIdentityClient { Id = "foreign-api", DisplayName = "Foreign", Audiences = [new IdentityClientAudience("api://foreign")] };
        var invalid = new StubIdentityClient { Id = "foreign-web", DisplayName = "Foreign", Grants = IdentityClientGrants.AuthorizationCode };

        // Act
        IdentityClientRegistry registry = IdentityClientRegistry.Create(_store, [foreign]);
        foreign.DisplayName = "Changed afterwards";
        ArgumentException refused = Should.Throw<ArgumentException>(() => IdentityClientRegistry.Create(_store, [invalid]));

        // Assert
        IIdentityClient locked = registry.Locked.Single();
        locked.ShouldBeOfType<IdentityClient>();
        locked.DisplayName.ShouldBe("Foreign");
        locked.IsLocked.ShouldBeTrue();
        refused.Message.ShouldContain("'foreign-web'", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - FindAsync: Should return a stored registration with the version it was read at")]
    public async Task FindAsync_WithAStoredRegistration_ShouldReturnItWithItsVersion()
    {
        // Arrange
        IdentityClientRegistry registry = IdentityClientRegistry.Create(_store);
        IdentityClientWriteResult added = await registry.TryAddAsync(TestClients.Api(), CancellationToken.None);

        // Act
        IIdentityClient? found = await registry.FindAsync("orders-api", CancellationToken.None);
        IIdentityClient? missing = await registry.FindAsync("no-such-client", CancellationToken.None);

        // Assert
        found.ShouldNotBeNull();
        found.IsLocked.ShouldBeFalse();
        found.Version.ShouldBe(added.Version);
        found.DisplayName.ShouldBe("Orders API");
        found.Scopes.Single().Name.Value.ShouldBe("orders.read");
        missing.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - FindAsync: Should refuse a default identifier and honor cancellation")]
    public async Task FindAsync_WithADefaultIdentifierOrACanceledToken_ShouldThrow()
    {
        // Arrange
        IdentityClientRegistry registry = IdentityClientRegistry.Create(_store, [TestClients.Api()]);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        // Act
        ArgumentException noId = await Should.ThrowAsync<ArgumentException>(async () => await registry.FindAsync(default));
        ArgumentException noAudience = await Should.ThrowAsync<ArgumentException>(async () => await registry.FindByAudienceAsync(default));

        // Assert
        noId.ParamName.ShouldBe("id");
        noAudience.ParamName.ShouldBe("audience");
        await Should.ThrowAsync<OperationCanceledException>(async () => await registry.FindAsync("orders-api", canceled.Token));
        await Should.ThrowAsync<OperationCanceledException>(async () => await registry.FindByAudienceAsync("api://orders", canceled.Token));
        await Should.ThrowAsync<OperationCanceledException>(async () => await registry.ListAsync(cancellationToken: canceled.Token));
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - FindAsync: Should report a stored registration that cannot be decoded as a corrupt record")]
    public async Task FindAsync_WithAStoredRecordThatIsNotARegistration_ShouldThrowRecordCorrupt()
    {
        // Arrange
        await _store.TryAddAsync(
            _clients,
            new IdentityHubRecord(new IdentityHubRecordKey("orders-api"), Encoding.UTF8.GetBytes("""{"v":7}""")),
            CancellationToken.None);
        IdentityClientRegistry registry = IdentityClientRegistry.Create(_store);

        // Act
        IdentityHubStoreException exception = await Should.ThrowAsync<IdentityHubStoreException>(
            async () => await registry.FindAsync("orders-api", CancellationToken.None));

        // Assert
        exception.Code.ShouldBe(IdentityHubErrorCode.StoreRecordCorrupt);
        exception.Key!.Value.Value.ShouldBe("orders-api");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - FindByAudienceAsync: Should find the stored registration that exposes an audience")]
    public async Task FindByAudienceAsync_WithAStoredAudience_ShouldReturnItsRegistration()
    {
        // Arrange
        IdentityClientRegistry registry = IdentityClientRegistry.Create(_store, [TestClients.Api()]);
        await registry.TryAddAsync(TestClients.Api("billing-api", "api://billing"), CancellationToken.None);

        // Act
        IIdentityClient? locked = await registry.FindByAudienceAsync("api://orders", CancellationToken.None);
        IIdentityClient? stored = await registry.FindByAudienceAsync("api://billing", CancellationToken.None);
        IIdentityClient? missing = await registry.FindByAudienceAsync("api://shipping", CancellationToken.None);
        IIdentityClient? otherCase = await registry.FindByAudienceAsync("api://Billing", CancellationToken.None);

        // Assert
        locked!.Id.Value.ShouldBe("orders-api");
        locked.IsLocked.ShouldBeTrue();
        stored!.Id.Value.ShouldBe("billing-api");
        stored.IsLocked.ShouldBeFalse();
        stored.Version.ShouldNotBeNull();
        missing.ShouldBeNull();
        otherCase.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - TryAddAsync: Should store a new registration and report its version")]
    public async Task TryAddAsync_WithANewRegistration_ShouldStoreIt()
    {
        // Arrange
        IdentityClientRegistry registry = IdentityClientRegistry.Create(_store);

        // Act
        IdentityClientWriteResult result = await registry.TryAddAsync(TestClients.Api(), CancellationToken.None);

        // Assert
        result.Status.ShouldBe(IdentityClientWriteStatus.Written);
        result.IsApplied.ShouldBeTrue();
        result.Version.Value.ShouldNotBeNull();
        _store.CountHeld(_clients).ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - TryAddAsync: Should answer Locked for an identifier code defines, without reaching the store")]
    public async Task TryAddAsync_WithAnIdentifierCodeDefines_ShouldAnswerLocked()
    {
        // Arrange
        var recording = new RecordingIdentityHubStore();
        IdentityClientRegistry registry = IdentityClientRegistry.Create(recording, [TestClients.Api()]);

        // Act
        IdentityClientWriteResult result = await registry.TryAddAsync(
            TestClients.Api(audience: "api://another"),
            CancellationToken.None);

        // Assert
        result.Status.ShouldBe(IdentityClientWriteStatus.Locked);
        result.IsApplied.ShouldBeFalse();
        recording.Calls.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - TryAddAsync: Should answer IdConflict for an identifier a stored registration has")]
    public async Task TryAddAsync_WithAStoredIdentifier_ShouldAnswerIdConflict()
    {
        // Arrange
        IdentityClientRegistry registry = IdentityClientRegistry.Create(_store);
        await registry.TryAddAsync(TestClients.Api(), CancellationToken.None);

        // Act
        IdentityClientWriteResult result = await registry.TryAddAsync(
            TestClients.Api(audience: "api://another"),
            CancellationToken.None);

        // Assert
        result.Status.ShouldBe(IdentityClientWriteStatus.IdConflict);
        (await registry.FindAsync("orders-api", CancellationToken.None))!.Audiences.Single().Value.ShouldBe("api://orders");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - TryAddAsync: Should answer AudienceConflict for an audience code exposes, without reaching the store")]
    public async Task TryAddAsync_WithAnAudienceCodeExposes_ShouldAnswerAudienceConflict()
    {
        // Arrange
        var recording = new RecordingIdentityHubStore();
        IdentityClientRegistry registry = IdentityClientRegistry.Create(recording, [TestClients.Api()]);

        // Act
        IdentityClientWriteResult result = await registry.TryAddAsync(
            TestClients.Api("orders-api-v2", "api://orders"),
            CancellationToken.None);

        // Assert
        result.Status.ShouldBe(IdentityClientWriteStatus.AudienceConflict);
        result.ConflictingAudience.Value.ShouldBe("api://orders");
        recording.Calls.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - TryAddAsync: Should answer AudienceConflict naming the audience another stored registration exposes")]
    public async Task TryAddAsync_WithAnAudienceAStoredRegistrationExposes_ShouldNameTheAudience()
    {
        // Arrange
        IdentityClientRegistry registry = IdentityClientRegistry.Create(_store);
        await registry.TryAddAsync(TestClients.Api(), CancellationToken.None);
        IdentityClient contender = IdentityClient.Create(new()
        {
            Id = "orders-api-v2",
            Audiences = { "api://orders-v2", "api://orders" },
        });

        // Act
        IdentityClientWriteResult result = await registry.TryAddAsync(contender, CancellationToken.None);

        // Assert
        result.Status.ShouldBe(IdentityClientWriteStatus.AudienceConflict);
        result.ConflictingAudience.Value.ShouldBe("api://orders");
        (await registry.FindAsync("orders-api-v2", CancellationToken.None)).ShouldBeNull();
        (await registry.FindByAudienceAsync("api://orders-v2", CancellationToken.None)).ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - TryAddAsync: Should refuse null and a registration that is not valid")]
    public async Task TryAddAsync_WithNullOrAnInvalidRegistration_ShouldThrow()
    {
        // Arrange
        IdentityClientRegistry registry = IdentityClientRegistry.Create(_store);
        var invalid = new StubIdentityClient { Id = "foreign-web", DisplayName = "Foreign", Grants = IdentityClientGrants.AuthorizationCode };

        // Act
        ArgumentNullException nullClient = await Should.ThrowAsync<ArgumentNullException>(
            async () => await registry.TryAddAsync(null!));
        await Should.ThrowAsync<ArgumentException>(async () => await registry.TryAddAsync(invalid));

        // Assert
        nullClient.ParamName.ShouldBe("client");
        _store.CountHeld(_clients).ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - TryAddAsync: Should store a registration that was read locked or stored elsewhere as a plain one")]
    public async Task TryAddAsync_WithARegistrationServedByAnotherRegistry_ShouldStoreItsContents()
    {
        // Arrange
        IdentityClientRegistry source = IdentityClientRegistry.Create(new InMemoryIdentityHubStore(), [TestClients.Api()]);
        IIdentityClient locked = (await source.FindAsync("orders-api", CancellationToken.None))!;
        IdentityClientRegistry registry = IdentityClientRegistry.Create(_store);

        // Act
        IdentityClientWriteResult result = await registry.TryAddAsync(locked, CancellationToken.None);
        IIdentityClient? stored = await registry.FindAsync("orders-api", CancellationToken.None);

        // Assert
        result.Status.ShouldBe(IdentityClientWriteStatus.Written);
        stored!.IsLocked.ShouldBeFalse();
        stored.Version.ShouldBe(result.Version);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - TryReplaceAsync: Should replace a stored registration read at the expected version")]
    public async Task TryReplaceAsync_WithTheExpectedVersion_ShouldReplaceTheRegistration()
    {
        // Arrange
        IdentityClientRegistry registry = IdentityClientRegistry.Create(_store);
        await registry.TryAddAsync(TestClients.Api(), CancellationToken.None);
        IIdentityClient read = (await registry.FindAsync("orders-api", CancellationToken.None))!;
        IdentityClientDescriptor descriptor = IdentityClientDescriptor.From(read);
        descriptor.DisplayName = "Orders API v2";
        descriptor.Scopes.Add(IdentityClientScope.Create("orders.write"));

        // Act
        IdentityClientWriteResult result = await registry.TryReplaceAsync(
            IdentityClient.Create(descriptor),
            read.Version!.Value,
            CancellationToken.None);
        IIdentityClient replaced = (await registry.FindAsync("orders-api", CancellationToken.None))!;

        // Assert
        result.Status.ShouldBe(IdentityClientWriteStatus.Written);
        result.Version.ShouldNotBe(read.Version!.Value);
        replaced.Version.ShouldBe(result.Version);
        replaced.DisplayName.ShouldBe("Orders API v2");
        replaced.Scopes.Count.ShouldBe(2);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - TryReplaceAsync: Should answer VersionConflict for a stale version and for a registration that is gone")]
    public async Task TryReplaceAsync_WithAStaleVersionOrNoRegistration_ShouldAnswerVersionConflict()
    {
        // Arrange
        IdentityClientRegistry registry = IdentityClientRegistry.Create(_store);
        IdentityClientWriteResult added = await registry.TryAddAsync(TestClients.Api(), CancellationToken.None);
        await registry.TryReplaceAsync(TestClients.Api(), added.Version, CancellationToken.None);

        // Act
        IdentityClientWriteResult stale = await registry.TryReplaceAsync(TestClients.Api(), added.Version, CancellationToken.None);
        IdentityClientWriteResult gone = await registry.TryReplaceAsync(
            TestClients.Api("billing-api", "api://billing"),
            added.Version,
            CancellationToken.None);

        // Assert
        stale.Status.ShouldBe(IdentityClientWriteStatus.VersionConflict);
        gone.Status.ShouldBe(IdentityClientWriteStatus.VersionConflict);
        (await registry.FindAsync("billing-api", CancellationToken.None)).ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - TryReplaceAsync: Should answer Locked for a registration code defines")]
    public async Task TryReplaceAsync_WithARegistrationCodeDefines_ShouldAnswerLocked()
    {
        // Arrange
        var recording = new RecordingIdentityHubStore();
        IdentityClientRegistry registry = IdentityClientRegistry.Create(recording, [TestClients.Api()]);

        // Act
        IdentityClientWriteResult result = await registry.TryReplaceAsync(TestClients.Api(), "1", CancellationToken.None);

        // Assert
        result.Status.ShouldBe(IdentityClientWriteStatus.Locked);
        recording.Calls.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - TryReplaceAsync: Should answer AudienceConflict for an audience another registration exposes")]
    public async Task TryReplaceAsync_WithAnAudienceAnotherRegistrationExposes_ShouldAnswerAudienceConflict()
    {
        // Arrange
        IdentityClientRegistry registry = IdentityClientRegistry.Create(_store, [TestClients.Api("locked-api", "api://locked")]);
        await registry.TryAddAsync(TestClients.Api("orders-api", "api://orders"), CancellationToken.None);
        IdentityClientWriteResult billing = await registry.TryAddAsync(
            TestClients.Api("billing-api", "api://billing"),
            CancellationToken.None);

        // Act
        IdentityClientWriteResult stored = await registry.TryReplaceAsync(
            TestClients.Api("billing-api", "api://orders"),
            billing.Version,
            CancellationToken.None);
        IdentityClientWriteResult locked = await registry.TryReplaceAsync(
            TestClients.Api("billing-api", "api://locked"),
            billing.Version,
            CancellationToken.None);

        // Assert
        stored.Status.ShouldBe(IdentityClientWriteStatus.AudienceConflict);
        stored.ConflictingAudience.Value.ShouldBe("api://orders");
        locked.Status.ShouldBe(IdentityClientWriteStatus.AudienceConflict);
        locked.ConflictingAudience.Value.ShouldBe("api://locked");
        (await registry.FindByAudienceAsync("api://billing", CancellationToken.None))!.Id.Value.ShouldBe("billing-api");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - TryReplaceAsync: Should free an audience the replacement no longer exposes")]
    public async Task TryReplaceAsync_DroppingAnAudience_ShouldLetAnotherRegistrationTakeIt()
    {
        // Arrange
        IdentityClientRegistry registry = IdentityClientRegistry.Create(_store);
        IdentityClientWriteResult added = await registry.TryAddAsync(TestClients.Api(), CancellationToken.None);

        // Act
        await registry.TryReplaceAsync(TestClients.Api("orders-api", "api://orders-v2"), added.Version, CancellationToken.None);
        IdentityClientWriteResult taken = await registry.TryAddAsync(
            TestClients.Api("legacy-api", "api://orders"),
            CancellationToken.None);

        // Assert
        taken.Status.ShouldBe(IdentityClientWriteStatus.Written);
        (await registry.FindByAudienceAsync("api://orders", CancellationToken.None))!.Id.Value.ShouldBe("legacy-api");
        (await registry.FindByAudienceAsync("api://orders-v2", CancellationToken.None))!.Id.Value.ShouldBe("orders-api");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - TryReplaceAsync: Should refuse null and a default version")]
    public async Task TryReplaceAsync_WithNullOrADefaultVersion_ShouldThrow()
    {
        // Arrange
        IdentityClientRegistry registry = IdentityClientRegistry.Create(_store);

        // Act
        ArgumentNullException nullClient = await Should.ThrowAsync<ArgumentNullException>(
            async () => await registry.TryReplaceAsync(null!, "1"));
        ArgumentException noVersion = await Should.ThrowAsync<ArgumentException>(
            async () => await registry.TryReplaceAsync(TestClients.Api(), default));

        // Assert
        nullClient.ParamName.ShouldBe("client");
        noVersion.ParamName.ShouldBe("expectedVersion");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - TryRemoveAsync: Should remove a stored registration read at the expected version")]
    public async Task TryRemoveAsync_WithTheExpectedVersion_ShouldRemoveTheRegistration()
    {
        // Arrange
        IdentityClientRegistry registry = IdentityClientRegistry.Create(_store);
        IdentityClientWriteResult added = await registry.TryAddAsync(TestClients.Api(), CancellationToken.None);

        // Act
        IdentityClientWriteResult stale = await registry.TryRemoveAsync("orders-api", "not-the-version", CancellationToken.None);
        IdentityClientWriteResult removed = await registry.TryRemoveAsync("orders-api", added.Version, CancellationToken.None);
        IdentityClientWriteResult again = await registry.TryRemoveAsync("orders-api", added.Version, CancellationToken.None);

        // Assert
        stale.Status.ShouldBe(IdentityClientWriteStatus.VersionConflict);
        removed.Status.ShouldBe(IdentityClientWriteStatus.Removed);
        removed.IsApplied.ShouldBeTrue();
        again.Status.ShouldBe(IdentityClientWriteStatus.VersionConflict);
        (await registry.FindAsync("orders-api", CancellationToken.None)).ShouldBeNull();
        (await registry.FindByAudienceAsync("api://orders", CancellationToken.None)).ShouldBeNull();
        _store.CountHeld(_clients).ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - TryRemoveAsync: Should answer Locked for a registration code defines")]
    public async Task TryRemoveAsync_WithARegistrationCodeDefines_ShouldAnswerLocked()
    {
        // Arrange
        var recording = new RecordingIdentityHubStore();
        IdentityClientRegistry registry = IdentityClientRegistry.Create(recording, [TestClients.Api()]);

        // Act
        IdentityClientWriteResult result = await registry.TryRemoveAsync("orders-api", "1", CancellationToken.None);

        // Assert
        result.Status.ShouldBe(IdentityClientWriteStatus.Locked);
        recording.Calls.ShouldBeEmpty();
        (await registry.FindAsync("orders-api", CancellationToken.None)).ShouldNotBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - TryRemoveAsync: Should refuse a default identifier and a default version")]
    public async Task TryRemoveAsync_WithADefaultArgument_ShouldThrowArgumentException()
    {
        // Arrange
        IdentityClientRegistry registry = IdentityClientRegistry.Create(_store);

        // Act
        ArgumentException noId = await Should.ThrowAsync<ArgumentException>(async () => await registry.TryRemoveAsync(default, "1"));
        ArgumentException noVersion = await Should.ThrowAsync<ArgumentException>(async () => await registry.TryRemoveAsync("orders-api", default));

        // Assert
        noId.ParamName.ShouldBe("id");
        noVersion.ParamName.ShouldBe("expectedVersion");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Shadowing: Should serve the registration code defines over a stored one with its identifier")]
    public async Task Shadowing_WithAStoredRegistrationCodeNowDefines_ShouldServeOnlyTheLockedOne()
    {
        // Arrange: a registration created at run time, then a deployment that defines it in code.
        IdentityClientRegistry before = IdentityClientRegistry.Create(_store);
        IdentityClientWriteResult added = await before.TryAddAsync(
            TestClients.Api("orders-api", "api://orders-runtime"),
            CancellationToken.None);
        IdentityClientRegistry after = IdentityClientRegistry.Create(_store, [TestClients.Api("orders-api", "api://orders")]);

        // Act
        IIdentityClient found = (await after.FindAsync("orders-api", CancellationToken.None))!;
        IIdentityClient? byShadowedAudience = await after.FindByAudienceAsync("api://orders-runtime", CancellationToken.None);
        IdentityClientPage page = await after.ListAsync(cancellationToken: CancellationToken.None);
        IdentityClientWriteResult remove = await after.TryRemoveAsync("orders-api", added.Version, CancellationToken.None);

        // Assert
        found.IsLocked.ShouldBeTrue();
        found.Audiences.Single().Value.ShouldBe("api://orders");
        byShadowedAudience.ShouldBeNull();
        page.Clients.Select(client => client.Id.Value).ShouldBe(["orders-api"]);
        page.Clients.Single().IsLocked.ShouldBeTrue();
        remove.Status.ShouldBe(IdentityClientWriteStatus.Locked);
        _store.CountHeld(_clients).ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Shadowing: Should serve the stored registration again once code no longer defines it")]
    public async Task Shadowing_WhenCodeNoLongerDefinesTheRegistration_ShouldServeTheStoredOneAgain()
    {
        // Arrange
        IdentityClientRegistry before = IdentityClientRegistry.Create(_store);
        await before.TryAddAsync(TestClients.Api("orders-api", "api://orders-runtime"), CancellationToken.None);
        IdentityClientRegistry.Create(_store, [TestClients.Api("orders-api", "api://orders")]);

        // Act
        IdentityClientRegistry rolledBack = IdentityClientRegistry.Create(_store);
        IIdentityClient found = (await rolledBack.FindAsync("orders-api", CancellationToken.None))!;

        // Assert
        found.IsLocked.ShouldBeFalse();
        found.Audiences.Single().Value.ShouldBe("api://orders-runtime");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - ListAsync: Should list the registrations defined in code first, then the stored ones")]
    public async Task ListAsync_WithLockedAndStoredRegistrations_ShouldListLockedFirst()
    {
        // Arrange
        IdentityClientRegistry registry = IdentityClientRegistry.Create(_store, [TestClients.Web(), TestClients.Api()]);
        await registry.TryAddAsync(TestClients.Api("billing-api", "api://billing"), CancellationToken.None);

        // Act
        IdentityClientPage page = await registry.ListAsync(cancellationToken: CancellationToken.None);

        // Assert
        page.Continuation.ShouldBeNull();
        page.Clients.Select(client => client.Id.Value).ShouldBe(["orders-api", "orders-web", "billing-api"]);
        page.Clients.Select(client => client.IsLocked).ShouldBe([true, true, false]);
        page.Clients[2].Version.ShouldNotBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [IdentityHub] - ListAsync: Should return every registration exactly once, whatever the page size")]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(7)]
    [InlineData(100)]
    public async Task ListAsync_PagingWithALimit_ShouldReturnEveryRegistrationOnce(int limit)
    {
        // Arrange: three defined in code, four stored, one of which code shadows.
        IdentityClientRegistry seed = IdentityClientRegistry.Create(_store);
        await seed.TryAddAsync(TestClients.Api("stored-1", "api://stored-1"), CancellationToken.None);
        await seed.TryAddAsync(TestClients.Api("stored-2", "api://stored-2"), CancellationToken.None);
        await seed.TryAddAsync(TestClients.Api("locked-2", "api://shadowed"), CancellationToken.None);
        await seed.TryAddAsync(TestClients.Api("stored-3", "api://stored-3"), CancellationToken.None);
        IdentityClientRegistry registry = IdentityClientRegistry.Create(
            _store,
            [
                TestClients.Api("locked-3", "api://locked-3"),
                TestClients.Api("locked-1", "api://locked-1"),
                TestClients.Api("locked-2", "api://locked-2"),
            ]);

        // Act
        List<IIdentityClient> listed = await ListAllAsync(registry, limit);

        // Assert
        listed.Select(client => client.Id.Value).Take(3).ShouldBe(["locked-1", "locked-2", "locked-3"]);
        listed.Select(client => client.Id.Value).Skip(3).OrderBy(id => id, StringComparer.Ordinal)
            .ShouldBe(["stored-1", "stored-2", "stored-3"]);
        listed.Count(client => client.IsLocked).ShouldBe(3);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - ListAsync: Should continue into the stored registrations after a page the locked ones fill")]
    public async Task ListAsync_WhenTheLockedRegistrationsFillAPage_ShouldContinueIntoTheStore()
    {
        // Arrange
        IdentityClientRegistry registry = IdentityClientRegistry.Create(_store, [TestClients.Api(), TestClients.Web()]);
        await registry.TryAddAsync(TestClients.Api("billing-api", "api://billing"), CancellationToken.None);

        // Act
        IdentityClientPage first = await registry.ListAsync(1, cancellationToken: CancellationToken.None);
        IdentityClientPage second = await registry.ListAsync(1, first.Continuation, CancellationToken.None);
        IdentityClientPage third = await registry.ListAsync(1, second.Continuation, CancellationToken.None);

        // Assert
        first.Clients.Single().Id.Value.ShouldBe("orders-api");
        first.Continuation.ShouldNotBeNull();
        second.Clients.Single().Id.Value.ShouldBe("orders-web");
        second.Continuation.ShouldNotBeNull();
        third.Clients.Single().Id.Value.ShouldBe("billing-api");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - ListAsync: Should list an empty registry as one empty last page")]
    public async Task ListAsync_WithNoRegistrations_ShouldReturnAnEmptyLastPage()
    {
        // Arrange
        IdentityClientRegistry registry = IdentityClientRegistry.Create(_store);

        // Act
        IdentityClientPage page = await registry.ListAsync(cancellationToken: CancellationToken.None);

        // Assert
        page.Clients.ShouldBeEmpty();
        page.Continuation.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - ListAsync: Should follow a store that returns short and empty pages")]
    public async Task ListAsync_OverAStoreThatPagesShort_ShouldStillReturnEveryRegistration()
    {
        // Arrange
        IdentityClientRegistry seed = IdentityClientRegistry.Create(_store);
        await seed.TryAddAsync(TestClients.Api("stored-1", "api://stored-1"), CancellationToken.None);
        await seed.TryAddAsync(TestClients.Api("stored-2", "api://stored-2"), CancellationToken.None);
        await seed.TryAddAsync(TestClients.Api("stored-3", "api://stored-3"), CancellationToken.None);
        IdentityClientRegistry registry = IdentityClientRegistry.Create(
            new ShortPageIdentityHubStore(_store),
            [TestClients.Api("locked-1", "api://locked-1")]);

        // Act
        List<IIdentityClient> listed = await ListAllAsync(registry, 10);
        IIdentityClient? byAudience = await registry.FindByAudienceAsync("api://stored-2", CancellationToken.None);

        // Assert
        listed.Select(client => client.Id.Value).OrderBy(id => id, StringComparer.Ordinal)
            .ShouldBe(["locked-1", "stored-1", "stored-2", "stored-3"]);
        byAudience!.Id.Value.ShouldBe("stored-2");
    }

    [Theory(DisplayName = "Cohesion Test [IdentityHub] - ListAsync: Should refuse a continuation it did not issue")]
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
        IdentityClientRegistry registry = IdentityClientRegistry.Create(_store, [TestClients.Api(), TestClients.Web()]);

        // Act
        IdentityHubStoreException exception = await Should.ThrowAsync<IdentityHubStoreException>(
            async () => await registry.ListAsync(10, continuation, CancellationToken.None));

        // Assert
        exception.Code.ShouldBe(IdentityHubErrorCode.StoreContinuationInvalid);
        exception.Set!.Value.Value.ShouldBe("clients");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - ListAsync: Should refuse a limit out of range and an empty continuation")]
    public async Task ListAsync_WithAnInvalidArgument_ShouldThrow()
    {
        // Arrange
        IdentityClientRegistry registry = IdentityClientRegistry.Create(_store);

        // Act
        ArgumentOutOfRangeException zero = await Should.ThrowAsync<ArgumentOutOfRangeException>(
            async () => await registry.ListAsync(0));
        ArgumentOutOfRangeException tooMany = await Should.ThrowAsync<ArgumentOutOfRangeException>(
            async () => await registry.ListAsync(IdentityClientPage.MaxLimit + 1));
        ArgumentException empty = await Should.ThrowAsync<ArgumentException>(
            async () => await registry.ListAsync(10, string.Empty));

        // Assert
        zero.ParamName.ShouldBe("limit");
        tooMany.ParamName.ShouldBe("limit");
        empty.ParamName.ShouldBe("continuation");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Writes: Should report a status the store cannot answer a write with as a store failure")]
    public async Task Writes_WhenTheStoreAnswersWithAnImpossibleStatus_ShouldThrowIdentityHubStoreException()
    {
        // Arrange
        var recording = new RecordingIdentityHubStore { WriteResult = IdentityHubWriteResult.VersionConflict };
        IdentityClientRegistry registry = IdentityClientRegistry.Create(recording);

        // Act
        IdentityHubStoreException add = await Should.ThrowAsync<IdentityHubStoreException>(
            async () => await registry.TryAddAsync(TestClients.Api(), CancellationToken.None));
        recording.WriteResult = IdentityHubWriteResult.KeyConflict;
        IdentityHubStoreException replace = await Should.ThrowAsync<IdentityHubStoreException>(
            async () => await registry.TryReplaceAsync(TestClients.Api(), "1", CancellationToken.None));

        // Assert
        add.Code.ShouldBe(IdentityHubErrorCode.Unknown);
        add.Set!.Value.Value.ShouldBe("clients");
        replace.Code.ShouldBe(IdentityHubErrorCode.Unknown);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Writes: Should pass the caller's cancellation token to the store")]
    public async Task Writes_WithACancellationToken_ShouldPassItToTheStore()
    {
        // Arrange
        var recording = new RecordingIdentityHubStore();
        IdentityClientRegistry registry = IdentityClientRegistry.Create(recording);
        using var source = new CancellationTokenSource();

        // Act
        await registry.TryAddAsync(TestClients.Api(), source.Token);

        // Assert
        recording.Calls.ShouldBe(["TryAddCoreAsync"]);
        recording.Set.Value.ShouldBe("clients");
        recording.Record!.Key.Value.ShouldBe("orders-api");
        recording.CancellationToken.ShouldBe(source.Token);
    }

    private static async Task<List<IIdentityClient>> ListAllAsync(IdentityClientRegistry registry, int limit)
    {
        var listed = new List<IIdentityClient>();
        string? continuation = null;
        int pages = 0;
        do
        {
            IdentityClientPage page = await registry.ListAsync(limit, continuation, CancellationToken.None);
            page.Clients.Count.ShouldBeLessThanOrEqualTo(limit);
            listed.AddRange(page.Clients);
            continuation = page.Continuation;
            (++pages).ShouldBeLessThan(100);
        }
        while (continuation is not null);

        return listed;
    }
}
