using System.Text.Json;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Storage.Tests;

public class StorageJsonContextTests
{
    [Fact(DisplayName = "Cohesion Test [Storage] - StorageJsonContext: Should read a volume and default its access mode")]
    public void Deserialize_Volume_ShouldDefaultAccessMode()
    {
        // Act
        var volume = JsonSerializer.Deserialize("""{ "capacityBytes": 10737418240 }""", StorageJsonContext.Default.VolumeSpec);

        // Assert
        volume.ShouldNotBeNull();
        volume.CapacityBytes.ShouldBe(10737418240);
        volume.AccessMode.ShouldBe(VolumeAccessMode.ReadWriteOnce);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - StorageJsonContext: Should refuse a volume without a capacity")]
    public void Deserialize_MissingCapacity_ShouldThrow()
    {
        // Act & Assert
        Should.Throw<JsonException>(() => JsonSerializer.Deserialize("""{ "accessMode": "ReadWriteMany" }""", StorageJsonContext.Default.VolumeSpec));
    }
}
