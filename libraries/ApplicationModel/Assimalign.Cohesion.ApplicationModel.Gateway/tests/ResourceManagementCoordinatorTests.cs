using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel.Gateway.Internal;
using Assimalign.Cohesion.ApplicationModel.Gateway.Tests.TestObjects;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Tests;

public sealed class ResourceManagementCoordinatorTests
{
    [Fact(DisplayName = "Cohesion Test [Gateway] - Startup discovery: binding-only manifests discover configured baseline and reconcile replicas")]
    public async Task Reconcile_WithRuntimeDiscoveredCatalog_ShouldPreserveConfiguredBaselineAcrossScaleOutAsync()
    {
        // Arrange
        using var fixture = new ResourceManagementFixture(10);
        fixture.SetBaseline("https://configured.example");

        // Act
        await fixture.Coordinator.ReconcileAsync("shop", "api", CancellationToken.None);
        ResourceStateOperation baseline = (await fixture.Coordinator.GetAsync(fixture.Change("ignored").Address, CancellationToken.None))!;
        ResourceStateOperation customized = await fixture.Coordinator.CommitAsync(fixture.Change("https://override.example") with { ExpectedRevision = baseline.Revision }, "operator", false, CancellationToken.None);
        fixture.AddInstance();
        await fixture.Coordinator.ReconcileAsync("shop", "api", CancellationToken.None);
        ResourceStateOperation current = (await fixture.Coordinator.GetAsync(customized.Address, CancellationToken.None))!;

        // Assert
        fixture.Coordinator.GetRuntimeManifest("shop", "api").GetProperty("states").GetArrayLength().ShouldBe(1);
        baseline.Desired.Value.GetProperty("origin").GetString().ShouldBe("https://configured.example");
        current.Status.ShouldBe(ResourceStateOperationStatus.Converged);
        current.Instances.Count.ShouldBe(11);
        current.Desired.Value.GetProperty("origin").GetString().ShouldBe("https://override.example");
    }

    [Fact(DisplayName = "Cohesion Test [Gateway] - Startup discovery: inconsistent configured baselines refuse fleet mutation")]
    public async Task Reconcile_WithDifferentStartupConfiguration_ShouldRejectBeforeApplyingAsync()
    {
        // Arrange
        using var fixture = new ResourceManagementFixture(2);
        fixture.SetBaseline("https://old.example");
        fixture.Protocol.CatalogOverrides["host-1"] = fixture.RuntimeCatalog;
        fixture.SetBaseline("https://new.example");

        // Act / Assert
        await Should.ThrowAsync<InvalidOperationException>(async () => await fixture.Coordinator.ReconcileAsync("shop", "api", CancellationToken.None));
        fixture.Protocol.ApplyCount.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Gateway] - Management: shared-state failover advances the backend fence")]
    public async Task Reconcile_WithUnavailableSharedExecutor_ShouldSelectAnotherAndAdvanceFenceAsync()
    {
        // Arrange
        using var fixture = new ResourceManagementFixture(10, shared: true);
        ResourceStateChange change = fixture.Change("https://portal.example");
        await fixture.Coordinator.CommitAsync(change, "operator", false, CancellationToken.None);
        fixture.Protocol.Unavailable.Add("host-1");

        // Act
        await fixture.Coordinator.ReconcileAsync("shop", "api", CancellationToken.None);

        // Assert
        fixture.Protocol.ApplyCount.ShouldBe(1);
        fixture.Protocol.LastFence.ShouldBe(2);
        (await fixture.Coordinator.GetAsync(change.Address, CancellationToken.None))!.Status.ShouldBe(ResourceStateOperationStatus.Converged);
    }

    [Fact(DisplayName = "Cohesion Test [Gateway] - Management: topology changes invalidate an in-flight convergence result")]
    public async Task Reconcile_WithMembershipChangingDuringApply_ShouldRecheckInventoryAsync()
    {
        // Arrange
        using var fixture = new ResourceManagementFixture();
        ResourceStateChange change = fixture.Change("https://portal.example");
        await fixture.Coordinator.CommitAsync(change, "operator", false, CancellationToken.None);
        fixture.Protocol.OnApply = fixture.AddInstance;

        // Act
        await fixture.Coordinator.ReconcileAsync("shop", "api", CancellationToken.None);

        // Assert
        (await fixture.Coordinator.GetAsync(change.Address, CancellationToken.None))!.Status.ShouldNotBe(ResourceStateOperationStatus.Converged);
        fixture.Protocol.OnApply = null;
        await fixture.Coordinator.ReconcileAsync("shop", "api", CancellationToken.None);
        (await fixture.Coordinator.GetAsync(change.Address, CancellationToken.None))!.Status.ShouldBe(ResourceStateOperationStatus.Converged);
    }

    [Fact(DisplayName = "Cohesion Test [Gateway] - Management: redeployment preserves overrides and clear restores the latest baseline")]
    public async Task Commit_WithBaselineChangeAndExplicitClear_ShouldRestoreLatestDeclarationAsync()
    {
        // Arrange
        using var fixture = new ResourceManagementFixture();
        fixture.SetBaseline("https://declared-one.example");
        ResourceStateAddress address = fixture.Change("ignored").Address;
        await fixture.Coordinator.ReconcileAsync("shop", "api", CancellationToken.None);
        ResourceStateOperation baseline = (await fixture.Coordinator.GetAsync(address, CancellationToken.None))!;
        ResourceStateOperation customized = await fixture.Coordinator.CommitAsync(
            fixture.Change("https://override.example") with { ExpectedRevision = baseline.Revision }, "operator", false, CancellationToken.None);

        // Act
        fixture.SetBaseline("https://declared-two.example");
        await fixture.Coordinator.ReconcileAsync("shop", "api", CancellationToken.None);
        (await fixture.Coordinator.GetAsync(address, CancellationToken.None))!.Desired.Value.GetProperty("origin").GetString().ShouldBe("https://override.example");
        ResourceStateChange clear = fixture.Change("ignored") with { ExpectedRevision = customized.Revision, ClearOverride = true };
        ResourceStateOperation restored = await fixture.Coordinator.CommitAsync(clear, "operator", false, CancellationToken.None);
        ResourceStateOperation retry = await fixture.Coordinator.CommitAsync(clear, "operator", false, CancellationToken.None);

        // Assert
        restored.Desired.Value.GetProperty("origin").GetString().ShouldBe("https://declared-two.example");
        retry.Id.ShouldBe(restored.Id);
        restored.Revision.ShouldBeGreaterThan(customized.Revision);
    }

    [Fact(DisplayName = "Cohesion Test [Gateway] - Management: Persists conditional intent and deduplicates retries across coordinator restart")]
    public async Task Commit_WithRetryAndRestart_ShouldReturnOriginalReceiptAsync()
    {
        // Arrange
        using var fixture = new ResourceManagementFixture();
        ResourceStateChange change = fixture.Change("https://one.example");

        // Act
        ResourceStateOperation first = await fixture.Coordinator.CommitAsync(change, "operator", false, CancellationToken.None);
        ResourceManagementCoordinator restarted = fixture.NewCoordinator();
        ResourceStateOperation retry = await restarted.CommitAsync(change, "operator", false, CancellationToken.None);

        // Assert
        retry.Id.ShouldBe(first.Id);
        retry.Revision.ShouldBe(first.Revision);
        retry.Address.ShouldBe(change.Address);
        (await restarted.GetAsync(change.Address, CancellationToken.None))!.Desired.Value.GetProperty("origin").GetString().ShouldBe("https://one.example");
        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await restarted.CommitAsync(change with { Desired = fixture.Value("https://different.example") }, "operator", false, CancellationToken.None));
        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await restarted.CommitAsync(fixture.Change("https://two.example"), "operator", false, CancellationToken.None));
    }

    [Fact(DisplayName = "Cohesion Test [Gateway] - Management: Reports partial delivery and reconciles HPA growth and process restart")]
    public async Task Reconcile_WithTenInstancesGrowthAndRestart_ShouldTrackActualProcessesAsync()
    {
        // Arrange
        using var fixture = new ResourceManagementFixture(10);
        fixture.Protocol.Unavailable.Add("host-10");
        ResourceStateChange change = fixture.Change("https://portal.example");
        ResourceStateOperation receipt = await fixture.Coordinator.CommitAsync(change, "operator", false, CancellationToken.None);

        // Act
        await fixture.Coordinator.ReconcileAsync("shop", "api", CancellationToken.None);
        ResourceStateOperation partial = (await fixture.Coordinator.GetAsync(change.Address, CancellationToken.None))!;
        fixture.Protocol.Unavailable.Clear();
        fixture.AddInstance();
        await fixture.Coordinator.ReconcileAsync("shop", "api", CancellationToken.None);
        ResourceStateOperation grown = (await fixture.Coordinator.GetAsync(change.Address, CancellationToken.None))!;
        Guid oldProcess = grown.Instances.Single(item => item.HostId == "host-1").ProcessStartId;
        fixture.Protocol.Restart("host-1");
        await fixture.Coordinator.ReconcileAsync("shop", "api", CancellationToken.None);
        ResourceStateOperation restarted = (await fixture.Coordinator.GetAsync(change.Address, CancellationToken.None))!;

        // Assert
        partial.Status.ShouldBe(ResourceStateOperationStatus.Degraded);
        partial.Instances.Count(item => item.AppliedRevision == receipt.Revision).ShouldBe(9);
        grown.Status.ShouldBe(ResourceStateOperationStatus.Converged);
        grown.Instances.Count.ShouldBe(11);
        grown.Instances.ShouldAllBe(item => item.AppliedRevision == receipt.Revision);
        restarted.Instances.Single(item => item.HostId == "host-1").ProcessStartId.ShouldNotBe(oldProcess);
        fixture.Protocol.ApplyCount.ShouldBe(12);
    }

    [Fact(DisplayName = "Cohesion Test [Gateway] - Management: Refuses convergence for incomplete membership")]
    public async Task Reconcile_WithIncompleteInventory_ShouldRemainDegradedAsync()
    {
        // Arrange
        using var fixture = new ResourceManagementFixture(9);
        fixture.Inventory = fixture.Inventory with { DesiredInstances = 10, IsComplete = false };
        ResourceStateChange change = fixture.Change("https://portal.example");
        await fixture.Coordinator.CommitAsync(change, "operator", false, CancellationToken.None);

        // Act
        await fixture.Coordinator.ReconcileAsync("shop", "api", CancellationToken.None);

        // Assert
        ResourceStateOperation operation = (await fixture.Coordinator.GetAsync(change.Address, CancellationToken.None))!;
        operation.Status.ShouldBe(ResourceStateOperationStatus.Degraded);
        operation.InventoryComplete.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Gateway] - Management: Shared state selects one executor instead of broadcasting")]
    public async Task Reconcile_WithSharedState_ShouldUseOneExecutorAsync()
    {
        // Arrange
        using var fixture = new ResourceManagementFixture(10, shared: true);
        ResourceStateChange change = fixture.Change("https://portal.example");
        await fixture.Coordinator.CommitAsync(change, "operator", false, CancellationToken.None);

        // Act
        await fixture.Coordinator.ReconcileAsync("shop", "api", CancellationToken.None);

        // Assert
        fixture.Protocol.ApplyCount.ShouldBe(1);
        (await fixture.Coordinator.GetAsync(change.Address, CancellationToken.None))!.Status.ShouldBe(ResourceStateOperationStatus.Converged);
    }
}
