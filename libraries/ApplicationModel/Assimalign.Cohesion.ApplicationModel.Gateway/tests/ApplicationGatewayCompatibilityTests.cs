using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel.Gateway.Internal;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Tests;

/// <summary>
/// What a gateway persists or derives must not change when its types are renamed: a resource's
/// revision and deployment id, and the record file. The expected values were captured from the
/// gateway at commit 607b27a53, before the ApplicationModel split of 2026-10-31.
/// </summary>
public sealed class ApplicationGatewayCompatibilityTests : IDisposable
{
    private const string DisplayPrefix = "Cohesion Test [ApplicationModel] - Gateway compatibility: ";

    private readonly TemporaryDirectory _directory = new();

    public void Dispose()
    {
        _directory.Dispose();
    }

    [Fact(DisplayName = DisplayPrefix + "A resource's revision and deployment id are those the gateway derived before the split")]
    public async Task StartAsync_WithFixedModel_ShouldDeriveThePinnedRevisionsAndDeployments()
    {
        // Arrange: a dependency with permissions, a literal setting, a setting filled in from the
        // dependency's output, and extensions for this platform and another.
        IApplicationModelBuilder builder = ApplicationModel.CreateBuilder("root");
        IApplicationResourceDefinition database = builder.AddResource("Database.Sql", "orders-db")
            .WithSetting("Collation", "en-US");
        builder.AddResource("Web", "orders-api")
            .DependsOn(database, "orders.read", "orders.write")
            .WithSetting("Greeting", "hello")
            .WithSetting("Database:Endpoint", database.Endpoint)
            .WithExtension("test", "replicas", JsonDocument.Parse("""{"count":3}""").RootElement)
            .WithExtension("kubernetes", "replicas", JsonDocument.Parse("""{"count":5}""").RootElement);
        IApplicationModel model = builder.Build();
        var infrastructure = new TestInfrastructure();
        var store = new InMemoryApplicationGatewayStore();

        // Act
        await using (await RunningGateway.StartAsync(infrastructure, store, model))
        {
        }

        ApplicationGatewayRecord record = (await store.LoadAsync("root")).ShouldNotBeNull();

        // Assert
        Describe(record, infrastructure, "orders-api").ShouldBe(
            "Web/orders-api 8f92b91e609921597a0eff014b1700091efcb9717b35b01f2110884f31db2cf3:1 098de5ad-8b3c-5660-aeb9-6a084f93d1a8");
        Describe(record, infrastructure, "orders-db").ShouldBe(
            "Database.Sql/orders-db 3ab1b1afbb330841d20d8c3d536435d7c36e78bbd019b5b38680fa2237a815c8:1 3994a0fb-c792-5197-b6fe-4d98355b9815");
    }

    [Fact(DisplayName = DisplayPrefix + "The record file keeps the keys the gateway wrote before the split")]
    public async Task SaveAsync_WithRecord_ShouldWriteThePinnedJson()
    {
        // Arrange
        JsonFileApplicationGatewayStore store = JsonFileApplicationGatewayStore.Create(_directory.FullPath);
        var instance = new ApplicationInstanceManifest(
            "Web",
            "orders-api",
            InstanceKey.Parse("01JBX3ZK7Q8W0Y5R4N2M6P9T1V"),
            DeploymentId.Parse("098de5ad-8b3c-5660-aeb9-6a084f93d1a8"),
            new Uri("inprocess://gw/orders-api"),
            [new ApplicationActionDescriptor("log-level", ApplicationActionKind.ReadWrite, true)]);
        var record = new ApplicationGatewayRecord("root", [
            new ApplicationGatewayDeploymentRecord(
                "Web",
                "orders-api",
                "abc:1",
                [ApplicationActionSnapshot.Create("log-level", Encoding.UTF8.GetBytes("""{"Level":"Debug"}"""))],
                [instance],
                new Dictionary<OutputName, string> { [OutputName.Endpoint] = "http://orders-api" })]);

        // Act
        await store.SaveAsync(record);
        string json = File.ReadAllText(Directory.GetFiles(_directory.FullPath, "*.json").ShouldHaveSingleItem()).ReplaceLineEndings("\n");

        // Assert
        json.ShouldBe("""
            {
              "gateway": "root",
              "resources": [
                {
                  "provider": "Web",
                  "name": "orders-api",
                  "revision": "abc:1",
                  "state": [
                    {
                      "action": "log-level",
                      "value": "eyJMZXZlbCI6IkRlYnVnIn0="
                    }
                  ],
                  "instances": [
                    {
                      "provider": "Web",
                      "name": "orders-api",
                      "key": "01JBX3ZK7Q8W0Y5R4N2M6P9T1V",
                      "deployment": "098de5ad-8b3c-5660-aeb9-6a084f93d1a8",
                      "controlPlaneEndpoint": "inprocess://gw/orders-api",
                      "actions": [
                        {
                          "name": "log-level",
                          "kind": "ReadWrite",
                          "isStateful": true
                        }
                      ]
                    }
                  ],
                  "outputs": {
                    "endpoint": "http://orders-api"
                  }
                }
              ],
              "epoch": 0
            }
            """.ReplaceLineEndings("\n"));
    }

    // The resource's provider and name, its recorded revision, and the deployment the gateway made.
    private static string Describe(ApplicationGatewayRecord record, TestInfrastructure infrastructure, ResourceName name)
    {
        ApplicationGatewayDeploymentRecord resource = record.Resources.Single(entry => entry.Name == name);
        DeploymentId deployment = infrastructure.Deployments.Single(entry => entry.Resource == name).Deployment;
        return $"{resource.Provider}/{resource.Name} {resource.Revision} {deployment}";
    }
}
