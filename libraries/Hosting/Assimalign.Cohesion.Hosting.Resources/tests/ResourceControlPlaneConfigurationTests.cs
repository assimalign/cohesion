using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting.Resources;
using Assimalign.Cohesion.Hosting.Resources.Internal;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Hosting.Resources.Tests;

public class ResourceControlPlaneConfigurationTests
{
    private const string DisplayPrefix = "Cohesion Test [Hosting.Resources] - Configuration: ";
    private const string PolicyPath = "Test:Policy";
    private const string LimitsPath = "Test:Limits";
    private const string DatabasesPath = "Test:Databases";

    [Fact(DisplayName = DisplayPrefix + "At startup every section applies, whatever its mutability")]
    public async Task ApplyConfigurationAsync_AtStartup_AppliesEverySection()
    {
        // Arrange
        (ResourceControlPlane plane, RecordingSectionTarget<TestPolicyOptions> policy, RecordingSectionTarget<TestLimitsOptions> limits) = CreatePlane();
        ResourceConfigurationRecord record = Record(deployment: """{"Test:Policy":{"origins":["https://b.example"]},"Test:Limits":{"maxConnections":200}}""");

        // Act
        ResourceConfigurationStatus status = await plane.ApplyConfigurationAsync(record, ResourceConfigurationPhase.Startup, CancellationToken.None);

        // Assert
        status.RecordDigest.ShouldBe(record.Digest);
        status.Sections.Select(section => section.State).ShouldBe([ResourceConfigurationState.Applied, ResourceConfigurationState.Applied]);
        policy.Current.Origins.ShouldBe(["https://b.example"]);
        limits.Current.MaxConnections.ShouldBe(200);
        plane.ConfigurationStatus.ShouldBeSameAs(status);
    }

    [Fact(DisplayName = DisplayPrefix + "While running, a live section applies and a restart section waits for a restart")]
    public async Task ApplyConfigurationAsync_WhileRunning_AppliesOnlyLiveSections()
    {
        // Arrange
        (ResourceControlPlane plane, RecordingSectionTarget<TestPolicyOptions> policy, RecordingSectionTarget<TestLimitsOptions> limits) = CreatePlane();
        await plane.ApplyConfigurationAsync(Record(deployment: """{"Test:Limits":{"maxConnections":200}}"""), ResourceConfigurationPhase.Startup, CancellationToken.None);
        ResourceConfigurationRecord changed = Record(
            deployment: """{"Test:Limits":{"maxConnections":200}}""",
            @override: """{"Test:Policy":{"origins":["https://partner.example"]},"Test:Limits":{"maxConnections":4000}}""");

        // Act
        ResourceConfigurationStatus status = await plane.ApplyConfigurationAsync(changed, ResourceConfigurationPhase.Running, CancellationToken.None);

        // Assert
        status.Sections[0].State.ShouldBe(ResourceConfigurationState.Applied);
        status.Sections[1].State.ShouldBe(ResourceConfigurationState.RestartRequired);
        policy.Current.Origins.ShouldBe(["https://partner.example"]);
        limits.Current.MaxConnections.ShouldBe(200);
    }

    [Fact(DisplayName = DisplayPrefix + "A later layer's shorter array replaces the earlier array")]
    public async Task ApplyConfigurationAsync_ShorterArrayInLaterLayer_ReplacesTheArray()
    {
        // Arrange
        (ResourceControlPlane plane, RecordingSectionTarget<TestPolicyOptions> policy, _) = CreatePlane(
            ["https://a.example", "https://b.example", "https://c.example"]);

        // Act
        await plane.ApplyConfigurationAsync(Record(deployment: """{"Test:Policy":{"origins":["https://x.example"]}}"""), ResourceConfigurationPhase.Startup, CancellationToken.None);

        // Assert
        policy.Current.Origins.ShouldBe(["https://x.example"]);
    }

    [Fact(DisplayName = DisplayPrefix + "A null member in the override removes the value the deployment set")]
    public async Task ApplyConfigurationAsync_NullMemberInOverride_RemovesTheDeploymentValue()
    {
        // Arrange
        (ResourceControlPlane plane, RecordingSectionTarget<TestPolicyOptions> policy, _) = CreatePlane();
        ResourceConfigurationRecord record = Record(
            deployment: """{"Test:Policy":{"maxAgeSeconds":60}}""",
            @override: """{"Test:Policy":{"maxAgeSeconds":null}}""");

        // Act
        ResourceConfigurationStatus status = await plane.ApplyConfigurationAsync(record, ResourceConfigurationPhase.Startup, CancellationToken.None);

        // Assert
        policy.Current.MaxAgeSeconds.ShouldBeNull();
        status.Sections[0].State.ShouldBe(ResourceConfigurationState.Baseline);
    }

    [Fact(DisplayName = DisplayPrefix + "A null section resets the section to its baseline")]
    public async Task ApplyConfigurationAsync_NullSection_ResetsToTheBaseline()
    {
        // Arrange
        (ResourceControlPlane plane, RecordingSectionTarget<TestPolicyOptions> policy, _) = CreatePlane();
        await plane.ApplyConfigurationAsync(Record(deployment: """{"Test:Policy":{"origins":["https://b.example"]}}"""), ResourceConfigurationPhase.Startup, CancellationToken.None);
        ResourceConfigurationRecord reset = Record(
            deployment: """{"Test:Policy":{"origins":["https://b.example"]}}""",
            @override: """{"Test:Policy":null}""");

        // Act
        ResourceConfigurationStatus status = await plane.ApplyConfigurationAsync(reset, ResourceConfigurationPhase.Running, CancellationToken.None);

        // Assert
        policy.Current.Origins.ShouldBe(["https://a.example"]);
        status.Sections[0].State.ShouldBe(ResourceConfigurationState.Baseline);
    }

    [Fact(DisplayName = DisplayPrefix + "Claim layers merge between the deployment and the override, in owner order")]
    public async Task ApplyConfigurationAsync_WithClaims_MergesEveryOwnersEntries()
    {
        // Arrange
        ResourceControlPlane plane = ResourceControlPlane.Create();
        var databases = new RecordingSectionTarget<Dictionary<string, TestDatabaseEntry>>(new Dictionary<string, TestDatabaseEntry>());
        plane.AddConfiguration(ResourceConfigurationSection.Create(
            DatabasesPath,
            TestSurfaceJsonContext.Default.DictionaryStringTestDatabaseEntry,
            ResourceConfigurationMutability.Live,
            new Dictionary<string, TestDatabaseEntry>(),
            databases.Apply,
            ResourceConfigurationScope.Backend,
            claimable: true));
        var claims = new List<KeyValuePair<string, byte[]>>
        {
            KeyValuePair.Create("billing", Utf8("""{"Test:Databases":{"ledger":{"engine":"sql"}}}""")),
            KeyValuePair.Create("audit", Utf8("""{"Test:Databases":{"trail":{"engine":"sql"}}}""")),
        };
        ResourceConfigurationRecord record = ResourceConfigurationRecord.Create(
            Utf8("""{"Test:Databases":{"orders":{"engine":"sql"}}}"""), claims, null);

        // Act
        await plane.ApplyConfigurationAsync(record, ResourceConfigurationPhase.Startup, CancellationToken.None);

        // Assert
        databases.Current.Keys.Order(StringComparer.Ordinal).ShouldBe(["ledger", "orders", "trail"]);
    }

    [Fact(DisplayName = DisplayPrefix + "A startup rejection keeps the baseline and holds readiness until the record is fixed")]
    public async Task ApplyConfigurationAsync_RejectedAtStartup_HoldsReadinessUntilFixed()
    {
        // Arrange
        (ResourceControlPlane plane, RecordingSectionTarget<TestPolicyOptions> policy, _) = CreatePlane();
        ResourceConfigurationRecord invalid = Record(deployment: """{"Test:Policy":{"origins":["*"],"allowCredentials":true}}""");

        // Act
        ResourceConfigurationStatus rejected = await plane.ApplyConfigurationAsync(invalid, ResourceConfigurationPhase.Startup, CancellationToken.None);
        ResourceHealthReport heldReadiness = await plane.CheckReadinessAsync(CancellationToken.None);
        ResourceHealthReport liveness = await plane.CheckLivenessAsync(CancellationToken.None);
        await plane.ApplyConfigurationAsync(Record(deployment: """{"Test:Policy":{"origins":["https://b.example"]}}"""), ResourceConfigurationPhase.Running, CancellationToken.None);
        ResourceHealthReport readiness = await plane.CheckReadinessAsync(CancellationToken.None);

        // Assert
        rejected.Sections[0].State.ShouldBe(ResourceConfigurationState.Rejected);
        rejected.Sections[0].Error.ShouldBe("wildcard-with-credentials");
        heldReadiness.Status.ShouldBe(HealthStatus.Unhealthy);
        liveness.Status.ShouldBe(HealthStatus.Degraded);
        liveness.Contributions[ResourceConfigurationHealthContributor.ContributorName].Description
            .ShouldBe(ResourceConfigurationHealthContributor.PendingDescription);
        readiness.Status.ShouldBe(HealthStatus.Healthy);
        policy.Current.Origins.ShouldBe(["https://b.example"]);
    }

    [Fact(DisplayName = DisplayPrefix + "A rejection while running keeps the last good value and the instance stays ready")]
    public async Task ApplyConfigurationAsync_RejectedWhileRunning_KeepsTheLastGoodValue()
    {
        // Arrange
        (ResourceControlPlane plane, RecordingSectionTarget<TestPolicyOptions> policy, _) = CreatePlane();
        await plane.ApplyConfigurationAsync(Record(deployment: """{"Test:Policy":{"origins":["https://b.example"]}}"""), ResourceConfigurationPhase.Startup, CancellationToken.None);
        ResourceConfigurationRecord invalid = Record(
            deployment: """{"Test:Policy":{"origins":["https://b.example"]}}""",
            @override: """{"Test:Policy":{"origins":["*"],"allowCredentials":true}}""");

        // Act
        ResourceConfigurationStatus status = await plane.ApplyConfigurationAsync(invalid, ResourceConfigurationPhase.Running, CancellationToken.None);
        ResourceHealthReport readiness = await plane.CheckReadinessAsync(CancellationToken.None);

        // Assert
        status.Sections[0].State.ShouldBe(ResourceConfigurationState.Rejected);
        policy.Current.Origins.ShouldBe(["https://b.example"]);
        readiness.Status.ShouldBe(HealthStatus.Degraded);
    }

    [Fact(DisplayName = DisplayPrefix + "An apply that throws is reported by exception type and keeps the value")]
    public async Task ApplyConfigurationAsync_ApplyThrows_ReportsTheExceptionType()
    {
        // Arrange
        ResourceControlPlane plane = ResourceControlPlane.Create();
        plane.AddConfiguration(ResourceConfigurationSection.Create<TestLimitsOptions>(
            LimitsPath,
            TestSurfaceJsonContext.Default.TestLimitsOptions,
            ResourceConfigurationMutability.Live,
            new TestLimitsOptions(),
            static _ => throw new InvalidOperationException("db-primary.corp.internal unreachable")));

        // Act
        ResourceConfigurationStatus status = await plane.ApplyConfigurationAsync(
            Record(deployment: """{"Test:Limits":{"maxConnections":5}}"""), ResourceConfigurationPhase.Startup, CancellationToken.None);

        // Assert
        status.Sections[0].State.ShouldBe(ResourceConfigurationState.Rejected);
        status.Sections[0].Error.ShouldBe("apply-failed: System.InvalidOperationException");
    }

    [Fact(DisplayName = DisplayPrefix + "A value that does not bind is rejected before the feature sees it")]
    public async Task ApplyConfigurationAsync_UnboundMember_RejectsTheSection()
    {
        // Arrange
        (ResourceControlPlane plane, RecordingSectionTarget<TestPolicyOptions> policy, _) = CreatePlane();

        // Act
        ResourceConfigurationStatus status = await plane.ApplyConfigurationAsync(
            Record(deployment: """{"Test:Policy":{"orgins":["https://b.example"]}}"""), ResourceConfigurationPhase.Startup, CancellationToken.None);

        // Assert
        status.Sections[0].State.ShouldBe(ResourceConfigurationState.Rejected);
        status.Sections[0].Error!.ShouldStartWith("invalid-value: ", Case.Sensitive);
        policy.Applies.ShouldBe(0);
    }

    [Fact(DisplayName = DisplayPrefix + "An unchanged record while running does not call the feature again")]
    public async Task ApplyConfigurationAsync_UnchangedRecordWhileRunning_DoesNotReapply()
    {
        // Arrange
        (ResourceControlPlane plane, RecordingSectionTarget<TestPolicyOptions> policy, _) = CreatePlane();
        ResourceConfigurationRecord record = Record(deployment: """{"Test:Policy":{"origins":["https://b.example"]}}""");
        await plane.ApplyConfigurationAsync(record, ResourceConfigurationPhase.Startup, CancellationToken.None);

        // Act
        await plane.ApplyConfigurationAsync(record, ResourceConfigurationPhase.Running, CancellationToken.None);
        await plane.ApplyConfigurationAsync(Record(deployment: """{"Test:Policy":{"origins":["https://b.example"]}}"""), ResourceConfigurationPhase.Running, CancellationToken.None);

        // Assert
        policy.Applies.ShouldBe(1);
    }

    [Fact(DisplayName = DisplayPrefix + "The surface freezes when the host is attached")]
    public void AddConfiguration_AfterAttachHost_ShouldThrow()
    {
        // Arrange
        (ResourceControlPlane plane, _, _) = CreatePlane();
        plane.AttachHost(new TestHost(new TestHostOptions()));

        // Act
        Should.Throw<InvalidOperationException>(() => plane.AddTool(ResourceTool.Create("sql", "admin", ResourceToolProtocol.WebSocket, "cohesion-sql.v1")));
        plane.Surface.Configuration.Count.ShouldBe(2);
    }

    [Fact(DisplayName = DisplayPrefix + "A section path is registered once")]
    public void AddConfiguration_DuplicatePath_ShouldThrow()
    {
        // Arrange
        (ResourceControlPlane plane, _, _) = CreatePlane();

        // Act
        Should.Throw<InvalidOperationException>(() => plane.AddConfiguration(ResourceConfigurationSection.Create(
            PolicyPath,
            TestSurfaceJsonContext.Default.TestPolicyOptions,
            ResourceConfigurationMutability.Live,
            new TestPolicyOptions(),
            static _ => null)));
    }

    [Fact(DisplayName = DisplayPrefix + "Before any record the status is empty and readiness is not held")]
    public async Task ConfigurationStatus_BeforeAnyRecord_IsEmpty()
    {
        // Arrange
        (ResourceControlPlane plane, _, _) = CreatePlane();

        // Act
        ResourceHealthReport readiness = await plane.CheckReadinessAsync(CancellationToken.None);

        // Assert
        plane.ConfigurationStatus.ShouldBeSameAs(ResourceConfigurationStatus.Empty);
        readiness.Status.ShouldBe(HealthStatus.Healthy);
    }

    private static (ResourceControlPlane Plane, RecordingSectionTarget<TestPolicyOptions> Policy, RecordingSectionTarget<TestLimitsOptions> Limits) CreatePlane(
        string[]? origins = null)
    {
        var baseline = new TestPolicyOptions { Origins = origins ?? ["https://a.example"] };
        var policy = new RecordingSectionTarget<TestPolicyOptions>(baseline, static value =>
            value.AllowCredentials && value.Origins.Contains("*") ? "wildcard-with-credentials" : null);
        var limits = new RecordingSectionTarget<TestLimitsOptions>(new TestLimitsOptions());
        ResourceControlPlane plane = ResourceControlPlane.Create();
        plane.AddConfiguration(ResourceConfigurationSection.Create(
            PolicyPath,
            TestSurfaceJsonContext.Default.TestPolicyOptions,
            ResourceConfigurationMutability.Live,
            baseline,
            policy.Apply));
        plane.AddConfiguration(ResourceConfigurationSection.Create(
            LimitsPath,
            TestSurfaceJsonContext.Default.TestLimitsOptions,
            ResourceConfigurationMutability.Restart,
            new TestLimitsOptions(),
            limits.Apply));
        return (plane, policy, limits);
    }

    private static ResourceConfigurationRecord Record(string? deployment = null, string? @override = null) =>
        ResourceConfigurationRecord.Create(
            deployment is null ? null : Utf8(deployment),
            Array.Empty<KeyValuePair<string, byte[]>>(),
            @override is null ? null : Utf8(@override));

    private static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value);
}
