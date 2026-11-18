using System.Text.Json;

using Assimalign.Cohesion.Fabric;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Networking.Tests;

public class NetworkingJsonContextTests
{
    [Fact(DisplayName = "Cohesion Test [Networking] - NetworkingJsonContext: Should read an exposure that targets a workload by reference")]
    public void Deserialize_Exposure_ShouldReadTargetAndVisibility()
    {
        // Arrange
        const string json = """{ "target": { "$ref": "Cohesion.Workloads/workloads/orders-api" }, "port": "http", "visibility": "public" }""";

        // Act
        var exposure = JsonSerializer.Deserialize(json, NetworkingJsonContext.Default.ExposureSpec);

        // Assert
        exposure.ShouldNotBeNull();
        exposure.Target.Type.ShouldBe(new ResourceTypeName("Cohesion.Workloads/workloads"));
        exposure.Port.ShouldBe("http");
        exposure.Visibility.ShouldBe(ExposureVisibility.Public);
    }

    [Fact(DisplayName = "Cohesion Test [Networking] - NetworkingJsonContext: Should refuse an unknown field")]
    public void Deserialize_UnknownField_ShouldThrow()
    {
        // Arrange
        const string json = """{ "target": { "$ref": "Cohesion.Workloads/workloads/orders-api" }, "port": "http", "host": "shop.acme.example" }""";

        // Act & Assert
        Should.Throw<JsonException>(() => JsonSerializer.Deserialize(json, NetworkingJsonContext.Default.ExposureSpec));
    }
}
