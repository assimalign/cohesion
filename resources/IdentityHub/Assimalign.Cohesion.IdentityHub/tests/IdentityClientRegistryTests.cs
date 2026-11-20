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
    // The identifier IdentityClientId.FromName derives from "orders-api", as a store key spells it.
    private const string ordersApiId = "bec0a3ee-3f3c-599e-bd0b-3b7194e035ba";

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
        IdentityClientRegistry reordered = IdentityClientRegistry.Create(
            _store,
            [TestClients.Api(), TestClients.Web(), TestClients.Daemon()]);

        // Assert
        IdentityClientId[] expected = InIdentifierOrder("orders-api", "orders-daemon", "orders-web");
        registry.Locked.Select(client => client.Id).ShouldBe(expected);
        reordered.Locked.Select(client => client.Id).ShouldBe(expected);
        registry.Locked.ShouldAllBe(client => client.IsLocked && client.Version == null);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Create: Should never write a registration defined in code to the store")]
    public async Task Create_WithRegistrationsDefinedInCode_ShouldLeaveTheStoreEmpty()
    {
        // Arrange
        var recording = new RecordingIdentityHubStore();

        // Act
        IdentityClientRegistry registry = IdentityClientRegistry.Create(recording, [TestClients.Api(), TestClients.Web()]);
        IdentityClient? found = await registry.FindAsync(IdentityClientId.FromName("orders-api"), CancellationToken.None);
        IdentityClient? byAudience = await registry.FindByAudienceAsync("api://orders", CancellationToken.None);

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
        repeated.Message.ShouldContain($"'Orders API ({IdentityClientId.FromName("orders-api")})'", Case.Sensitive);
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
        exception.Message.ShouldContain($"'Orders API ({IdentityClientId.FromName("orders-api")})'", Case.Sensitive);
        exception.Message.ShouldContain($"'Orders API ({IdentityClientId.FromName("orders-api-v2")})'", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Create: Should refuse a permission that another registration defined in code does not expose")]
    public void Create_WithAPermissionTheResourceDoesNotExpose_ShouldThrowArgumentException()
    {
        // Arrange
        IdentityClient web = IdentityClient.Create(new()
        {
            Id = IdentityClientId.FromName("orders-web"),
            Permissions = { IdentityClientPermission.Delegated(IdentityClientId.FromName("orders-api"), "orders.write") },
        });
        IdentityClient daemon = IdentityClient.Create(new()
        {
            Id = IdentityClientId.FromName("orders-daemon"),
            Permissions = { IdentityClientPermission.Application(IdentityClientId.FromName("orders-api"), "orders.read") },
        });

        // Act
        ArgumentException scope = Should.Throw<ArgumentException>(
            () => IdentityClientRegistry.Create(_store, [TestClients.Api(), web]));
        ArgumentException role = Should.Throw<ArgumentException>(
            () => IdentityClientRegistry.Create(_store, [TestClients.Api(), daemon]));

        // Assert
        scope.ParamName.ShouldBe("clients");
        scope.Message.ShouldContain($"'{IdentityClientId.FromName("orders-web")}'", Case.Sensitive);
        scope.Message.ShouldContain("'orders.write'", Case.Sensitive);
        scope.Message.ShouldContain($"'Orders API ({IdentityClientId.FromName("orders-api")})'", Case.Sensitive);
        role.Message.ShouldContain($"'{IdentityClientId.FromName("orders-daemon")}'", Case.Sensitive);
        role.Message.ShouldContain("'orders.read'", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Create: Should leave a permission on a registration that code does not define unresolved")]
    public void Create_WithAPermissionOnARegistrationCodeDoesNotDefine_ShouldCreate()
    {
        // Act
        IdentityClientRegistry registry = IdentityClientRegistry.Create(_store, [TestClients.Web(api: "stored-api")]);

        // Assert
        registry.Locked.Single().Permissions.Single().Resource.ShouldBe(IdentityClientId.FromName("stored-api"));
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - FindAsync: Should return a stored registration with the version it was read at")]
    public async Task FindAsync_WithAStoredRegistration_ShouldReturnItWithItsVersion()
    {
        // Arrange
        IdentityClientRegistry registry = IdentityClientRegistry.Create(_store);
        IdentityClientWriteResult added = await registry.TryAddAsync(TestClients.Api(), CancellationToken.None);

        // Act
        IdentityClient? found = await registry.FindAsync(IdentityClientId.FromName("orders-api"), CancellationToken.None);
        IdentityClient? missing = await registry.FindAsync(IdentityClientId.FromName("no-such-client"), CancellationToken.None);

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
        await Should.ThrowAsync<OperationCanceledException>(async () => await registry.FindAsync(IdentityClientId.FromName("orders-api"), canceled.Token));
        await Should.ThrowAsync<OperationCanceledException>(async () => await registry.FindByAudienceAsync("api://orders", canceled.Token));
        await Should.ThrowAsync<OperationCanceledException>(async () => await registry.ListAsync(cancellationToken: canceled.Token));
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - FindAsync: Should report a stored registration that cannot be decoded as a corrupt record")]
    public async Task FindAsync_WithAStoredRecordThatIsNotARegistration_ShouldThrowRecordCorrupt()
    {
        // Arrange
        await _store.TryAddAsync(
            _clients,
            new IdentityHubRecord(new IdentityHubRecordKey(ordersApiId), Encoding.UTF8.GetBytes("""{"v":7}""")),
            CancellationToken.None);
        IdentityClientRegistry registry = IdentityClientRegistry.Create(_store);

        // Act
        IdentityHubStoreException exception = await Should.ThrowAsync<IdentityHubStoreException>(
            async () => await registry.FindAsync(IdentityClientId.FromName("orders-api"), CancellationToken.None));

        // Assert
        exception.Code.ShouldBe(IdentityHubErrorCode.StoreRecordCorrupt);
        exception.Key!.Value.Value.ShouldBe(ordersApiId);
    }

    [Theory(DisplayName = "Cohesion Test [IdentityHub] - ListAsync: Should report a stored record whose key is not the one spelling of an identifier as a corrupt record")]
    [InlineData("orders-api")]
    [InlineData("BEC0A3EE-3F3C-599E-BD0B-3B7194E035BA")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task ListAsync_WithAStoredKeyThatIsNoClientIdentifier_ShouldThrowRecordCorrupt(string key)
    {
        // Arrange
        await _store.TryAddAsync(
            _clients,
            new IdentityHubRecord(
                new IdentityHubRecordKey(key),
                Encoding.UTF8.GetBytes("""{"v":1,"displayName":"orders","accessTokenLifetime":"01:00:00"}""")),
            CancellationToken.None);
        IdentityClientRegistry registry = IdentityClientRegistry.Create(_store);

        // Act
        IdentityHubStoreException exception = await Should.ThrowAsync<IdentityHubStoreException>(
            async () => await registry.ListAsync(cancellationToken: CancellationToken.None));

        // Assert
        exception.Code.ShouldBe(IdentityHubErrorCode.StoreRecordCorrupt);
        exception.Key!.Value.Value.ShouldBe(key);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - FindByAudienceAsync: Should find the stored registration that exposes an audience")]
    public async Task FindByAudienceAsync_WithAStoredAudience_ShouldReturnItsRegistration()
    {
        // Arrange
        IdentityClientRegistry registry = IdentityClientRegistry.Create(_store, [TestClients.Api()]);
        await registry.TryAddAsync(TestClients.Api("billing-api", "api://billing"), CancellationToken.None);

        // Act
        IdentityClient? locked = await registry.FindByAudienceAsync("api://orders", CancellationToken.None);
        IdentityClient? stored = await registry.FindByAudienceAsync("api://billing", CancellationToken.None);
        IdentityClient? missing = await registry.FindByAudienceAsync("api://shipping", CancellationToken.None);
        IdentityClient? otherCase = await registry.FindByAudienceAsync("api://Billing", CancellationToken.None);

        // Assert
        locked!.Id.ShouldBe(IdentityClientId.FromName("orders-api"));
        locked.IsLocked.ShouldBeTrue();
        stored!.Id.ShouldBe(IdentityClientId.FromName("billing-api"));
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
        (await registry.FindAsync(IdentityClientId.FromName("orders-api"), CancellationToken.None))!.Audiences.Single().Value.ShouldBe("api://orders");
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
            Id = IdentityClientId.FromName("orders-api-v2"),
            Audiences = { "api://orders-v2", "api://orders" },
        });

        // Act
        IdentityClientWriteResult result = await registry.TryAddAsync(contender, CancellationToken.None);

        // Assert
        result.Status.ShouldBe(IdentityClientWriteStatus.AudienceConflict);
        result.ConflictingAudience.Value.ShouldBe("api://orders");
        (await registry.FindAsync(IdentityClientId.FromName("orders-api-v2"), CancellationToken.None)).ShouldBeNull();
        (await registry.FindByAudienceAsync("api://orders-v2", CancellationToken.None)).ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - TryAddAsync: Should refuse a null registration")]
    public async Task TryAddAsync_WithANullRegistration_ShouldThrowArgumentNullException()
    {
        // Arrange
        IdentityClientRegistry registry = IdentityClientRegistry.Create(_store);

        // Act
        ArgumentNullException nullClient = await Should.ThrowAsync<ArgumentNullException>(
            async () => await registry.TryAddAsync(null!));

        // Assert
        nullClient.ParamName.ShouldBe("client");
        _store.CountHeld(_clients).ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - TryAddAsync: Should store a registration that was read locked or stored elsewhere as a plain one")]
    public async Task TryAddAsync_WithARegistrationServedByAnotherRegistry_ShouldStoreItsContents()
    {
        // Arrange
        IdentityClientRegistry source = IdentityClientRegistry.Create(new InMemoryIdentityHubStore(), [TestClients.Api()]);
        IdentityClient locked = (await source.FindAsync(IdentityClientId.FromName("orders-api"), CancellationToken.None))!;
        IdentityClientRegistry registry = IdentityClientRegistry.Create(_store);

        // Act
        IdentityClientWriteResult result = await registry.TryAddAsync(locked, CancellationToken.None);
        IdentityClient? stored = await registry.FindAsync(IdentityClientId.FromName("orders-api"), CancellationToken.None);

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
        IdentityClient read = (await registry.FindAsync(IdentityClientId.FromName("orders-api"), CancellationToken.None))!;
        IdentityClientDescriptor descriptor = IdentityClientDescriptor.From(read);
        descriptor.DisplayName = "Orders API v2";
        descriptor.Scopes.Add(IdentityClientScope.Create("orders.write"));

        // Act
        IdentityClientWriteResult result = await registry.TryReplaceAsync(
            IdentityClient.Create(descriptor),
            read.Version!.Value,
            CancellationToken.None);
        IdentityClient replaced = (await registry.FindAsync(IdentityClientId.FromName("orders-api"), CancellationToken.None))!;

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
        (await registry.FindAsync(IdentityClientId.FromName("billing-api"), CancellationToken.None)).ShouldBeNull();
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
        (await registry.FindByAudienceAsync("api://billing", CancellationToken.None))!.Id.ShouldBe(IdentityClientId.FromName("billing-api"));
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
        (await registry.FindByAudienceAsync("api://orders", CancellationToken.None))!.Id.ShouldBe(IdentityClientId.FromName("legacy-api"));
        (await registry.FindByAudienceAsync("api://orders-v2", CancellationToken.None))!.Id.ShouldBe(IdentityClientId.FromName("orders-api"));
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
        IdentityClientWriteResult stale = await registry.TryRemoveAsync(IdentityClientId.FromName("orders-api"), "not-the-version", CancellationToken.None);
        IdentityClientWriteResult removed = await registry.TryRemoveAsync(IdentityClientId.FromName("orders-api"), added.Version, CancellationToken.None);
        IdentityClientWriteResult again = await registry.TryRemoveAsync(IdentityClientId.FromName("orders-api"), added.Version, CancellationToken.None);

        // Assert
        stale.Status.ShouldBe(IdentityClientWriteStatus.VersionConflict);
        removed.Status.ShouldBe(IdentityClientWriteStatus.Removed);
        removed.IsApplied.ShouldBeTrue();
        again.Status.ShouldBe(IdentityClientWriteStatus.VersionConflict);
        (await registry.FindAsync(IdentityClientId.FromName("orders-api"), CancellationToken.None)).ShouldBeNull();
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
        IdentityClientWriteResult result = await registry.TryRemoveAsync(IdentityClientId.FromName("orders-api"), "1", CancellationToken.None);

        // Assert
        result.Status.ShouldBe(IdentityClientWriteStatus.Locked);
        recording.Calls.ShouldBeEmpty();
        (await registry.FindAsync(IdentityClientId.FromName("orders-api"), CancellationToken.None)).ShouldNotBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - TryRemoveAsync: Should refuse a default identifier and a default version")]
    public async Task TryRemoveAsync_WithADefaultArgument_ShouldThrowArgumentException()
    {
        // Arrange
        IdentityClientRegistry registry = IdentityClientRegistry.Create(_store);

        // Act
        ArgumentException noId = await Should.ThrowAsync<ArgumentException>(async () => await registry.TryRemoveAsync(default, "1"));
        ArgumentException noVersion = await Should.ThrowAsync<ArgumentException>(async () => await registry.TryRemoveAsync(IdentityClientId.FromName("orders-api"), default));

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
        IdentityClient found = (await after.FindAsync(IdentityClientId.FromName("orders-api"), CancellationToken.None))!;
        IdentityClient? byShadowedAudience = await after.FindByAudienceAsync("api://orders-runtime", CancellationToken.None);
        IdentityClientPage page = await after.ListAsync(cancellationToken: CancellationToken.None);
        IdentityClientWriteResult remove = await after.TryRemoveAsync(IdentityClientId.FromName("orders-api"), added.Version, CancellationToken.None);

        // Assert
        found.IsLocked.ShouldBeTrue();
        found.Audiences.Single().Value.ShouldBe("api://orders");
        byShadowedAudience.ShouldBeNull();
        page.Clients.Select(client => client.Id).ShouldBe(Ids("orders-api"));
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
        IdentityClient found = (await rolledBack.FindAsync(IdentityClientId.FromName("orders-api"), CancellationToken.None))!;

        // Assert
        found.IsLocked.ShouldBeFalse();
        found.Audiences.Single().Value.ShouldBe("api://orders-runtime");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Adoption: Should lock a registration created at run time once code defines its name")]
    public async Task Adoption_WhenCodeDefinesTheNameARegistrationWasCreatedUnder_ShouldServeTheCodeDefinitionLocked()
    {
        // Arrange: a registration created at run time under a name, with a secret issued at run
        // time, then a deployment whose code defines that name in another case.
        IdentityClientRegistry before = IdentityClientRegistry.Create(_store);
        IdentityClientWriteResult created = await before.TryAddAsync(
            IdentityClient.Create(new()
            {
                Id = IdentityClientId.FromName("Orders-Daemon"),
                DisplayName = "Created at run time",
                Grants = IdentityClientGrants.ClientCredentials,
                Credentials = { IdentityClientSecretCredential.Generate("issued-at-run-time", out string issued) },
            }),
            CancellationToken.None);
        IdentityClientSecretCredential deployed = IdentityClientSecretCredential.Generate("deployed", out string fromDeployment);
        IdentityClient defined = IdentityClient.Create(new()
        {
            Id = IdentityClientId.FromName("orders-daemon"),
            DisplayName = "Orders daemon",
            Grants = IdentityClientGrants.ClientCredentials,
            Credentials = { deployed },
        });

        // Act
        IdentityClientRegistry after = IdentityClientRegistry.Create(_store, [defined]);
        IdentityClient served = (await after.FindAsync(IdentityClientId.FromName("ORDERS-DAEMON"), CancellationToken.None))!;
        IdentityClientPage page = await after.ListAsync(cancellationToken: CancellationToken.None);
        IdentityClientWriteResult replace = await after.TryReplaceAsync(defined, created.Version, CancellationToken.None);
        IdentityClientWriteResult remove = await after.TryRemoveAsync(defined.Id, created.Version, CancellationToken.None);
        IdentityClientWriteResult add = await after.TryAddAsync(defined, CancellationToken.None);

        // Assert: code wins whole, so the secret issued at run time is no longer accepted.
        created.Status.ShouldBe(IdentityClientWriteStatus.Written);
        served.IsLocked.ShouldBeTrue();
        served.Version.ShouldBeNull();
        served.DisplayName.ShouldBe("Orders daemon");
        var secret = served.Credentials.Single().ShouldBeOfType<IdentityClientSecretCredential>();
        secret.Id.Value.ShouldBe("deployed");
        secret.Verify(fromDeployment, DateTimeOffset.UtcNow).ShouldBeTrue();
        secret.Verify(issued, DateTimeOffset.UtcNow).ShouldBeFalse();
        page.Clients.Single().ShouldBeSameAs(served);
        replace.Status.ShouldBe(IdentityClientWriteStatus.Locked);
        remove.Status.ShouldBe(IdentityClientWriteStatus.Locked);
        add.Status.ShouldBe(IdentityClientWriteStatus.Locked);
        _store.CountHeld(_clients).ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Adoption: Should leave a registration created under a random identifier to the API")]
    public async Task Adoption_WithARegistrationCreatedUnderARandomIdentifier_ShouldNotLockIt()
    {
        // Arrange
        IdentityClientRegistry before = IdentityClientRegistry.Create(_store);
        IdentityClientId random = IdentityClientId.New();
        await before.TryAddAsync(
            IdentityClient.Create(new() { Id = random, DisplayName = "orders-api", Audiences = { "api://orders-runtime" } }),
            CancellationToken.None);

        // Act
        IdentityClientRegistry after = IdentityClientRegistry.Create(_store, [TestClients.Api()]);
        IdentityClient stored = (await after.FindAsync(random, CancellationToken.None))!;
        IdentityClientPage page = await after.ListAsync(cancellationToken: CancellationToken.None);

        // Assert
        stored.IsLocked.ShouldBeFalse();
        stored.Version.ShouldNotBeNull();
        page.Clients.Select(client => client.Id).ShouldBe([IdentityClientId.FromName("orders-api"), random]);
        page.Clients.Select(client => client.IsLocked).ShouldBe([true, false]);
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
        page.Clients.Select(client => client.Id).ShouldBe([.. InIdentifierOrder("orders-api", "orders-web"), IdentityClientId.FromName("billing-api")]);
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
        List<IdentityClient> listed = await ListAllAsync(registry, limit);

        // Assert
        listed.Select(client => client.Id).Take(3).ShouldBe(InIdentifierOrder("locked-1", "locked-2", "locked-3"));
        listed.Select(client => client.Id).Skip(3).ShouldBe(Ids("stored-1", "stored-2", "stored-3"), ignoreOrder: true);
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
        first.Clients.Single().Id.ShouldBe(IdentityClientId.FromName("orders-api"));
        first.Continuation.ShouldNotBeNull();
        second.Clients.Single().Id.ShouldBe(IdentityClientId.FromName("orders-web"));
        second.Continuation.ShouldNotBeNull();
        third.Clients.Single().Id.ShouldBe(IdentityClientId.FromName("billing-api"));
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
        List<IdentityClient> listed = await ListAllAsync(registry, 10);
        IdentityClient? byAudience = await registry.FindByAudienceAsync("api://stored-2", CancellationToken.None);

        // Assert
        listed.Select(client => client.Id).ShouldBe(Ids("locked-1", "stored-1", "stored-2", "stored-3"), ignoreOrder: true);
        byAudience!.Id.ShouldBe(IdentityClientId.FromName("stored-2"));
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
        recording.Record!.Key.Value.ShouldBe(ordersApiId);
        recording.CancellationToken.ShouldBe(source.Token);
    }

    private static IdentityClientId[] Ids(params string[] names)
    {
        return names.Select(name => IdentityClientId.FromName(name)).ToArray();
    }

    // The order the registry documents for its locked registrations: the ordinal order of the
    // identifiers' text. Sorting the text here keeps the expectation apart from Guid.CompareTo.
    private static IdentityClientId[] InIdentifierOrder(params string[] names)
    {
        return Ids(names).OrderBy(id => id.ToString(), StringComparer.Ordinal).ToArray();
    }

    private static async Task<List<IdentityClient>> ListAllAsync(IdentityClientRegistry registry, int limit)
    {
        var listed = new List<IdentityClient>();
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
