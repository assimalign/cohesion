using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.ApplicationModel.Gateway.Tests.TestObjects;
using Assimalign.Cohesion.Hosting.Resources.Internal;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Tests;

public sealed class ResourceManagementStartupTests
{
    [Fact(DisplayName = "Cohesion Test [Gateway] - Discovery: absent and empty configured baselines have equal meaning")]
    public async Task Commit_AbsentAndEmptyBaselines_ShouldAcceptEquivalentCatalogs()
    {
        using var fixture = new ResourceManagementFixture(2);
        JsonNode catalog = JsonNode.Parse(fixture.RuntimeCatalog.GetRawText())!;
        catalog.AsObject().Remove("initialState");
        using JsonDocument omitted = JsonDocument.Parse(catalog.ToJsonString());
        JsonElement withoutInitialState = omitted.RootElement.Clone();
        fixture.Protocol.Catalog = target => target.HostId == "host-1" ? fixture.RuntimeCatalog : withoutInitialState;

        ResourceStateOperation accepted = await fixture.Coordinator.CommitAsync(
            fixture.Change("https://override.example"), "operator", false, CancellationToken.None);

        accepted.Status.ShouldBe(ResourceStateOperationStatus.Accepted);
        fixture.Protocol.Admissions.Count.ShouldBe(2);
        fixture.Protocol.Admissions.ShouldAllBe(admission => admission.States.Count == 0);
        fixture.Protocol.ApplyCount.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Gateway] - Discovery: catalog declaration order does not create a state conflict")]
    public async Task Commit_ReorderedCatalog_ShouldAcceptEquivalentDefinitionsAndBaselines()
    {
        using var fixture = new ResourceManagementFixture(2);
        fixture.SetBaseline("https://configured.example");
        JsonElement normal = AddedStateCatalog(fixture.RuntimeCatalog, reverse: false);
        JsonElement reversed = AddedStateCatalog(fixture.RuntimeCatalog, reverse: true);
        fixture.Protocol.Catalog = target => target.HostId == "host-1" ? normal : reversed;

        ResourceStateOperation accepted = await fixture.Coordinator.CommitAsync(
            fixture.Change("https://override.example"), "operator", false, CancellationToken.None);

        accepted.Status.ShouldBe(ResourceStateOperationStatus.Accepted);
        fixture.Coordinator.GetRuntimeManifest("shop", "api").GetProperty("states").GetArrayLength().ShouldBe(2);
        fixture.Protocol.ApplyCount.ShouldBe(0);
        fixture.Protocol.Admissions.Count.ShouldBe(2);
        fixture.Protocol.Admissions.ShouldAllBe(admission => admission.States.Count == 2 && admission.States.All(state => state.Revision == 0));
    }

    [Fact(DisplayName = "Cohesion Test [Gateway] - Discovery: membership changes during describe cannot publish a stale catalog")]
    public async Task Reconcile_TopologyChangesDuringDescribe_ShouldKeepPreviousCatalogUntilRetry()
    {
        using var fixture = new ResourceManagementFixture();
        fixture.SetBaseline("https://old.example");
        await fixture.Coordinator.ReconcileAsync("shop", "api", CancellationToken.None);
        fixture.SetBaseline("https://new.example");
        fixture.Protocol.OnDescribe = () =>
        {
            fixture.Protocol.OnDescribe = null;
            fixture.AddInstance();
        };

        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await fixture.Coordinator.ReconcileAsync("shop", "api", CancellationToken.None));

        fixture.Coordinator.GetRuntimeManifest("shop", "api").GetProperty("initialState")[0]
            .GetProperty("desired").GetProperty("value").GetProperty("origin").GetString().ShouldBe("https://old.example");
        await fixture.Coordinator.ReconcileAsync("shop", "api", CancellationToken.None);
        fixture.Coordinator.GetRuntimeManifest("shop", "api").GetProperty("initialState")[0]
            .GetProperty("desired").GetProperty("value").GetProperty("origin").GetString().ShouldBe("https://new.example");
    }

    [Fact(DisplayName = "Cohesion Test [Gateway] - Discovery: conflicting catalogs retain every durable slot in process admission")]
    public async Task Commit_AddedStateConflict_ShouldAdmitConfiguredStateWithoutDroppingAcceptedOverride()
    {
        using var fixture = new ResourceManagementFixture(2);
        fixture.SetBaseline("https://configured.example");
        ResourceStateChange change = fixture.Change("https://override.example");
        ResourceStateOperation accepted = await fixture.Coordinator.CommitAsync(change, "operator", false, CancellationToken.None);
        fixture.Protocol.Admissions.Clear();
        fixture.Protocol.CatalogOverrides["host-2"] = AddedStateCatalog(fixture.RuntimeCatalog, reverse: false);

        await Should.ThrowAsync<InvalidOperationException>(async () => await fixture.Coordinator.CommitAsync(
            fixture.Change("https://must-not-commit.example") with { ExpectedRevision = accepted.Revision }, "operator", false, CancellationToken.None));

        fixture.Protocol.Admissions.Count.ShouldBe(2);
        foreach (var admission in fixture.Protocol.Admissions)
        {
            ResourceBootstrapState persisted = admission.States.Single(state => state.Name.Value == "web.cors");
            persisted.Revision.ShouldBe(accepted.Revision);
            persisted.ValueDigest.ShouldBe(ResourceStateWire.ComputeValueDigest(true, change.Desired.Value));
        }
        var newer = fixture.Protocol.Admissions.Single(admission => admission.HostId == "host-2");
        newer.States.Single(state => state.Name.Value == "web.added").Revision.ShouldBe(0);
        fixture.Protocol.Admissions.Single(admission => admission.HostId == "host-1").States.Count.ShouldBe(1);
        ResourceStateOperation current = (await fixture.Coordinator.GetAsync(change.Address, CancellationToken.None))!;
        current.Id.ShouldBe(accepted.Id);
        (await fixture.Coordinator.GetAsync(new ResourceStateAddress("shop", "api", "web.added", "configuration"), CancellationToken.None)).ShouldBeNull();
        fixture.Protocol.ApplyCount.ShouldBe(0);
    }

    private static JsonElement AddedStateCatalog(JsonElement original, bool reverse)
    {
        JsonNode catalog = JsonNode.Parse(original.GetRawText())!;
        JsonNode first = catalog["states"]![0]!.DeepClone();
        JsonNode added = first.DeepClone();
        added["name"] = "web.added";
        added["payloadSchemaId"] = "urn:test:added:v1";
        using JsonDocument definition = JsonDocument.Parse(added.ToJsonString());
        JsonElement signed = ResourceStateWire.WithContractDigest(definition.RootElement);
        added = JsonNode.Parse(signed.GetRawText())!;
        JsonNode baseline = catalog["initialState"]![0]!.DeepClone();
        JsonNode addedBaseline = baseline.DeepClone();
        addedBaseline["name"] = "web.added";
        addedBaseline["desired"]!["value"]!["origin"] = "https://added.example";
        catalog["states"] = reverse ? new JsonArray(added, first) : new JsonArray(first, added);
        catalog["initialState"] = reverse ? new JsonArray(addedBaseline, baseline) : new JsonArray(baseline, addedBaseline);
        using JsonDocument result = JsonDocument.Parse(catalog.ToJsonString());
        return result.RootElement.Clone();
    }
}
