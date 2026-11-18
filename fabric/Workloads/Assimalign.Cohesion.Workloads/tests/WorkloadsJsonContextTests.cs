using System.Linq;
using System.Text.Json;

using Assimalign.Cohesion.Fabric;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Workloads.Tests;

public class WorkloadsJsonContextTests
{
    private const string spec = """
        {
          "source": { "image": "registry.acme.example/orders-api@sha256:9b1e" },
          "replicas": 3,
          "settings": {
            "Orders:Database": { "$ref": "Cohesion.Database/databases/orders-db", "path": "outputs.connectionString" },
            "Cors:Origins": "https://shop.acme.example"
          },
          "ports": [ { "name": "http", "port": 8080 } ],
          "mounts": [ { "volume": { "$ref": "Cohesion.Storage/volumes/orders-data" }, "path": "/data" } ],
          "platforms": { "kubernetes": { "apiVersion": "2026-12-01", "nodeSelector": { "pool": "general" } } }
        }
        """;

    [Fact(DisplayName = "Cohesion Test [Workloads] - WorkloadsJsonContext: Should read literals, references and platform blocks")]
    public void Deserialize_Spec_ShouldReadEveryPart()
    {
        // Act
        var workload = JsonSerializer.Deserialize(spec, WorkloadsJsonContext.Default.WorkloadSpec);

        // Assert
        workload.ShouldNotBeNull();
        workload.Source.Image.ShouldBe("registry.acme.example/orders-api@sha256:9b1e");
        workload.Replicas.ShouldBe(3);
        workload.Settings["Orders:Database"].Ref!.Path.ShouldBe("outputs.connectionString");
        workload.Settings["Cors:Origins"].Literal.ShouldBe("https://shop.acme.example");
        workload.Mounts.Single().Volume.Name.ShouldBe(new ResourceName("orders-data"));
        workload.Platforms[new PlatformName("kubernetes")].GetProperty("apiVersion").GetString().ShouldBe("2026-12-01");
    }

    [Fact(DisplayName = "Cohesion Test [Workloads] - WorkloadsJsonContext: Should refuse an unknown portable field")]
    public void Deserialize_UnknownField_ShouldThrow()
    {
        // Arrange
        var misspelled = spec.Replace("\"replicas\"", "\"replica\"");

        // Act & Assert
        Should.Throw<JsonException>(() => JsonSerializer.Deserialize(misspelled, WorkloadsJsonContext.Default.WorkloadSpec));
    }
}
