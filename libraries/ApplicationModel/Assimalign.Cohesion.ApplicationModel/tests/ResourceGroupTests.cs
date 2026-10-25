using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.ApplicationModel.Tests;

public sealed class ResourceGroupTests
{
    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Resource groups: logical membership preserves dependencies, manifests and deployment plans")]
    public void Build_LogicalGroups_ShouldPreserveDeploymentAndDependencies()
    {
        IApplicationBuilder builder = CreateBuilder();
        IApplicationResourceDescriptor? database = null;
        builder.AddResourceGroup("data", group => database = group.AddResource(TestManifestFactory.Create("database")));
        builder.AddResourceGroup("frontend", group =>
            group.AddResource(TestManifestFactory.Create("api")).DependsOn(database!));
        builder.AddResourceGroup("empty", _ => { });
        builder.AddResource(TestManifestFactory.Create("worker"));
        database!.ResourceGroup.ShouldBe((ResourceGroupName)"data");

        IApplicationModel model = builder.Build().Model;

        model.ResourceGroups.Select(group => group.Name.Value).ShouldBe(["data", "frontend", "empty"]);
        model.ResourceGroups[0].Resources.Select(name => name.Value).ShouldBe(["database"]);
        model.ResourceGroups[1].Resources.Select(name => name.Value).ShouldBe(["api"]);
        model.ResourceGroups[2].Resources.ShouldBeEmpty();
        model.Descriptors[1].Dependencies.ShouldHaveSingleItem().ShouldBeSameAs(model.Descriptors[0]);
        model.Descriptors[0].ResourceGroup.ShouldBe((ResourceGroupName)"data");
        model.Descriptors[1].ResourceGroup.ShouldBe((ResourceGroupName)"frontend");
        model.Descriptors[2].ResourceGroup.ShouldBeNull();
        model.Plans[0].ResourceGroup.ShouldBe((ResourceGroupName)"data");
        model.Plans[1].ResourceGroup.ShouldBe((ResourceGroupName)"frontend");
        model.Plans[2].ResourceGroup.ShouldBeNull();

        IApplicationBuilder baselineBuilder = CreateBuilder();
        baselineBuilder.AddResource(TestManifestFactory.Create("database"));
        IApplicationModel baseline = baselineBuilder.Build().Model;
        ResourceManifestCanonicalizer.ComputeHash(model.Manifests[0])
            .ShouldBe(ResourceManifestCanonicalizer.ComputeHash(baseline.Manifests[0]));
        JsonSerializer.Serialize(model.Plans[0] with { ResourceGroup = null }, ResourcePlanJsonContext.Default.ResourcePlan)
            .ShouldBe(JsonSerializer.Serialize(baseline.Plans[0], ResourcePlanJsonContext.Default.ResourcePlan));
        using JsonDocument serializedPlan = JsonDocument.Parse(
            JsonSerializer.Serialize(model.Plans[0], ResourcePlanJsonContext.Default.ResourcePlan));
        serializedPlan.RootElement.GetProperty("resourceGroup").GetString().ShouldBe("data");
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Resource groups: existing application-builder extension members work inside groups")]
    public void AddResourceGroup_ExistingExtension_ShouldRetainMembership()
    {
        IApplicationBuilder builder = CreateBuilder();
        builder.AddResource(TestManifestFactory.Create("worker"));
        builder.AddResourceGroup("dependencies", group =>
            group.RemoteReference("remote", remote => remote.Endpoint("http", "http://127.0.0.1:8081")));

        IApplicationModel model = builder.Build().Model;

        model.ResourceGroups.ShouldHaveSingleItem().Resources.Select(name => name.Value).ShouldBe(["remote"]);
        model.Descriptors[1].ResourceGroup.ShouldBe((ResourceGroupName)"dependencies");
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Resource groups: snapshots stay immutable as the builder adds more groups")]
    public void Build_GroupSnapshot_ShouldRemainImmutable()
    {
        IApplicationBuilder builder = CreateBuilder();
        builder.AddResourceGroup("frontend", group => group.AddResource(TestManifestFactory.Create("api")));
        IApplicationModel model = builder.Build().Model;

        builder.AddResourceGroup("jobs", group => group.AddResource(TestManifestFactory.Create("worker")));

        model.ResourceGroups.ShouldHaveSingleItem().Name.ShouldBe((ResourceGroupName)"frontend");
        model.Resources.Count.ShouldBe(1);
        Should.Throw<NotSupportedException>(() => ((IList<ResourceName>)model.ResourceGroups[0].Resources).Add("worker"));
        builder.Build().Model.ResourceGroups.Count.ShouldBe(2);
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Resource groups: duplicate names and nesting fail without leaking the active scope")]
    public void AddResourceGroup_InvalidScopes_ShouldRejectAndRestoreUngroupedState()
    {
        IApplicationBuilder builder = CreateBuilder();
        builder.AddResourceGroup("frontend", group => group.AddResource(TestManifestFactory.Create("api")));
        Should.Throw<InvalidOperationException>(() => builder.AddResourceGroup("frontend", _ => { }));
        Should.Throw<InvalidOperationException>(() => builder.AddResourceGroup("outer", group =>
            group.AddResourceGroup("inner", _ => { })));
        IApplicationResourceDescriptor worker = builder.AddResource(TestManifestFactory.Create("worker"));
        worker.ResourceGroup.ShouldBeNull();
        Should.Throw<InvalidOperationException>(() => builder.AddResourceGroup("second", group =>
            group.AddResource(TestManifestFactory.Create("api"))));
        builder.Build().Model.ResourceGroups.Select(group => group.Name.Value).ShouldNotContain("inner");
    }

    [Theory(DisplayName = "Cohesion Test [ApplicationModel] - Resource groups: names are validated ASCII segments")]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("frontend/api")]
    [InlineData("frontend%2Fapi")]
    public void ResourceGroupName_InvalidValue_ShouldFail(string name)
    {
        ResourceGroupName.IsValid(name, out _).ShouldBeFalse();
        Should.Throw<ArgumentException>(() => CreateBuilder().AddResourceGroup(name, _ => { }));
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Resource groups: describe and export round trips preserve groups and membership")]
    public void Documents_GroupedModel_ShouldRoundTrip()
    {
        IApplicationModel model = CreateGroupedModel();
        string json = Describe(model);
        IApplicationModel restored = ApplicationModelDocument.Parse(json).ToModel();
        restored.ResourceGroups.Select(group => group.Name.Value).ShouldBe(["frontend", "empty"]);
        restored.ResourceGroups[0].Resources.Select(name => name.Value).ShouldBe(["api"]);
        restored.Descriptors[0].ResourceGroup.ShouldBe((ResourceGroupName)"frontend");
        restored.Plans[0].ResourceGroup.ShouldBe((ResourceGroupName)"frontend");
        Describe(restored).ShouldBe(json);

        ApplicationExportDocument export = ApplicationExportDocument.Create(model, "1");
        using var stream = new MemoryStream();
        export.Save(stream);
        ApplicationExportDocument parsed = ApplicationExportDocument.Parse(Encoding.UTF8.GetString(stream.ToArray()));
        parsed.Resources[0].ResourceGroup.ShouldBe("frontend");
        parsed.ToModel().ResourceGroups[0].Resources.Select(name => name.Value).ShouldBe(["api"]);
    }

    [Theory(DisplayName = "Cohesion Test [ApplicationModel] - Resource groups: inconsistent portable membership is rejected")]
    [InlineData("unknown-member")]
    [InlineData("duplicate-member")]
    [InlineData("duplicate-group")]
    [InlineData("descriptor-mismatch")]
    [InlineData("plan-mismatch")]
    public void Parse_InconsistentGroups_ShouldReject(string defect)
    {
        JsonNode document = JsonNode.Parse(Describe(CreateGroupedModel()))!;
        JsonArray groups = document["resourceGroups"]!.AsArray();
        JsonNode resource = document["resources"]![0]!;
        switch (defect)
        {
            case "unknown-member": groups[0]!["resources"]![0] = "missing"; break;
            case "duplicate-member": groups[0]!["resources"]!.AsArray().Insert(0, JsonValue.Create("api")); break;
            case "duplicate-group": groups.Insert(groups.Count, groups[0]!.DeepClone()); break;
            case "descriptor-mismatch": resource["resourceGroup"] = "other"; break;
            case "plan-mismatch": resource["plan"]!["resourceGroup"] = "other"; break;
        }

        Should.Throw<InvalidDataException>(() => ApplicationModelDocument.Parse(document.ToJsonString()));
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Resource groups: old documents remain ungrouped and export summaries cannot contradict membership")]
    public void Parse_LegacyAndContradictoryExports_ShouldEnforceMembership()
    {
        IApplicationBuilder builder = CreateBuilder();
        builder.AddResource(TestManifestFactory.Create("api"));
        JsonNode legacy = JsonNode.Parse(Describe(builder.Build().Model))!;
        legacy.AsObject().Remove("resourceGroups");
        ApplicationModelDocument.Parse(legacy.ToJsonString()).ToModel().ResourceGroups.ShouldBeEmpty();

        using var stream = new MemoryStream();
        ApplicationExportDocument.Create(CreateGroupedModel(), "1").Save(stream);
        JsonNode export = JsonNode.Parse(stream.ToArray())!;
        export["resources"]![0]!["resourceGroup"] = "other";
        Should.Throw<InvalidDataException>(() => ApplicationExportDocument.Parse(export.ToJsonString()));
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Resource groups: application-set provider binding preserves logical groups")]
    public async Task ApplicationSet_ProviderWrapper_ShouldPreserveGroups()
    {
        IApplicationModel model = ApplicationModelDocument.Parse(Describe(CreateGroupedModel())).ToModel();
        var gateway = new RecordingSetGateway();
        IApplicationSet set = Application.CreateSet(gateway, ["--mode=apply", "--environment=Development"])
            .AddApplication(new ApplicationDeclaration(model.Name, new FixedModelResolver(model)), _ => { });

        await set.RunAsync();

        IApplicationModel bound = gateway.ReconciledModels.ShouldNotBeNull().ShouldHaveSingleItem();
        bound.ResourceGroups.ShouldBeSameAs(model.ResourceGroups);
        bound.Descriptors[0].ResourceGroup.ShouldBe((ResourceGroupName)"frontend");
    }

    private static IApplicationBuilder CreateBuilder() => Application.CreateBuilder((ApplicationName)"appa", ["--environment=Development"])
        .UseGateway(new FakeGateway());

    private static IApplicationModel CreateGroupedModel()
    {
        IApplicationBuilder builder = CreateBuilder();
        builder.AddResourceGroup("frontend", group => group.AddResource(TestManifestFactory.Create("api")));
        builder.AddResourceGroup("empty", _ => { });
        return builder.Build().Model;
    }

    private static string Describe(IApplicationModel model)
    {
        using var stream = new MemoryStream();
        ApplicationModelDocument.Create(model).Save(stream);
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
