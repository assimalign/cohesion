using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.ApplicationModel.Gateway.Internal;
using Assimalign.Cohesion.Hosting.Resources.Internal;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Tests.TestObjects;

internal sealed class ResourceManagementFixture : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "cohesion-resource-management-" + Guid.NewGuid().ToString("N"));
    private JsonElement _runtime;

    public ResourceManagementFixture(int instances = 1, bool shared = false)
    {
        Store = FileResourceManagementStore.Create(_directory);
        using JsonDocument document = JsonDocument.Parse("""
            {"schema":"cohesion/resource-runtime/v1","management":{"endpoint":"https","path":"/cohesion/v1","protocol":"cohesion/resource-state/v1"},
             "states":[{"name":"web.cors","payloadSchemaId":"urn:test:cors:v1","scope":"everyInstance","contractDigest":"test-contract","uses":["management","orchestration"],
             "supportsAbsence":false,"keySchema":{"const":"configuration"},"payloadSchema":{"type":"object","properties":{"origin":{"type":"string"}},"additionalProperties":false}}],"initialState":[]}
            """.Replace("everyInstance", shared ? "sharedState" : "everyInstance", StringComparison.Ordinal));
        string contractDigest = ResourceStateWire.ComputeDigest(document.RootElement.GetProperty("states")[0], "contractDigest");
        using JsonDocument valid = JsonDocument.Parse(document.RootElement.GetRawText().Replace("test-contract", contractDigest, StringComparison.Ordinal));
        _runtime = valid.RootElement.Clone();
        Protocol.Catalog = _ => _runtime.Clone();
        Inventory = new ResourceInstanceInventory(Guid.NewGuid(), "0", 0, true, Array.Empty<ResourceManagementTarget>());
        for (int index = 0; index < instances; index++) { AddInstance(); }
        Coordinator = NewCoordinator();
    }

    public IResourceManagementStore Store { get; }
    public ResourceManagementCoordinator Coordinator { get; }
    public ResourceManagementFixtureProtocol Protocol { get; } = new();
    public ResourceInstanceInventory Inventory { get; set; }
    public JsonElement RuntimeCatalog => _runtime.Clone();

    public void AddInstance()
    {
        int count = Inventory.Instances.Count + 1;
        var target = new ResourceManagementTarget("host-" + count, "generation-1", new Uri("https://10.0.0." + count + ":9443/cohesion/v1"));
        Inventory = Inventory with { Revision = count.ToString(), DesiredInstances = count, Instances = Inventory.Instances.Append(target).ToArray() };
    }

    public ResourceManagementCoordinator NewCoordinator() => new(Store, Protocol, TimeProvider.System,
        (_, _) => DeclaredRuntime(), (_, _, _) => ValueTask.FromResult(Inventory));

    private JsonElement DeclaredRuntime()
    {
        using JsonDocument document = JsonDocument.Parse("{\"schema\":\"cohesion/resource-runtime/v1\",\"management\":" + _runtime.GetProperty("management").GetRawText() + "}");
        return document.RootElement.Clone();
    }

    public void SetBaseline(string origin)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (JsonProperty property in _runtime.EnumerateObject())
            {
                if (property.Name != "initialState") { property.WriteTo(writer); }
            }
            writer.WriteStartArray("initialState");
            writer.WriteStartObject();
            writer.WriteString("name", "web.cors");
            writer.WriteString("key", "configuration");
            writer.WritePropertyName("desired");
            JsonSerializer.Serialize(writer, Value(origin), ResourceManagementJsonContext.Default.ResourceDesiredValue);
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        using JsonDocument document = JsonDocument.Parse(stream.ToArray());
        _runtime = document.RootElement.Clone();
    }

    public ResourceStateChange Change(string origin) => new(Guid.NewGuid(), new ResourceStateAddress("shop", "api", "web.cors", "configuration"), null, Value(origin));
    public ResourceDesiredValue Value(string origin)
    {
        using JsonDocument document = JsonDocument.Parse("{\"origin\":\"" + origin + "\"}");
        return new ResourceDesiredValue(true, document.RootElement.Clone());
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) { Directory.Delete(_directory, recursive: true); }
    }
}
