using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting.Resources;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Tests;

public sealed class FileResourceRecordStoreTests
{
    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Records: a written record reads back, and an instance computes the same digest from its files")]
    public async Task TryWriteAsync_ThenRead_ShouldRoundTripAndMatchTheInstanceDigest()
    {
        // Arrange
        string root = CreateDirectory();
        try
        {
            FileResourceRecordStore store = FileResourceRecordStore.Create(root);
            ResourceRecord record = ResourceRecord.Create(
                "0123456789abcdef",
                Json("""{"Web:Cors":{"origins":["https://a.example.com"]}}"""),
                new Dictionary<ApplicationName, ReadOnlyMemory<byte>> { ["appb"] = Json("""{"Orders:Schema":{"invoices":{"columns":4}}}""") },
                Json("""{"Web:Cors":{"maxAgeSeconds":60}}"""));

            // Act
            bool written = await store.TryWriteAsync("appa", "api", record, expectedVersion: null);
            ResourceRecord? read = await store.ReadAsync("appa", "api");
            ResourceConfigurationRecord instance = ResourceConfigurationRecord.Load(store.GetInstanceDirectory("appa", "api"));

            // Assert
            written.ShouldBeTrue();
            read.ShouldNotBeNull();
            read.Generation.ShouldBe("0123456789abcdef");
            read.Version.ShouldBe(record.Version);
            read.Claims.Keys.ShouldBe([(ApplicationName)"appb"]);
            instance.Digest.ShouldBe(record.Digest);
            instance.ClaimOwners.ShouldBe(["appb"]);
            Directory.GetFiles(store.GetInstanceDirectory("appa", "api")).Length.ShouldBe(3);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Records: a write naming a version that moved is refused")]
    public async Task TryWriteAsync_WithStaleVersion_ShouldRefuse()
    {
        // Arrange
        string root = CreateDirectory();
        try
        {
            FileResourceRecordStore store = FileResourceRecordStore.Create(root);
            ResourceRecord first = ResourceRecord.Create("g1", default, new Dictionary<ApplicationName, ReadOnlyMemory<byte>>(), default);
            ResourceRecord second = ResourceRecord.Create("g1", default, new Dictionary<ApplicationName, ReadOnlyMemory<byte>>(), Json("""{"A":{}}"""));
            (await store.TryWriteAsync("appa", "api", first, null)).ShouldBeTrue();
            (await store.TryWriteAsync("appa", "api", second, first.Version)).ShouldBeTrue();

            // Act
            bool stale = await store.TryWriteAsync("appa", "api", first, first.Version);

            // Assert
            stale.ShouldBeFalse();
            (await store.ReadAsync("appa", "api"))!.Version.ShouldBe(second.Version);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Records: layers a record lacks are removed, and an empty record still delivers a directory")]
    public async Task TryWriteAsync_WithoutLayers_ShouldRemoveTheirFiles()
    {
        // Arrange
        string root = CreateDirectory();
        try
        {
            FileResourceRecordStore store = FileResourceRecordStore.Create(root);
            await store.TryWriteAsync("appa", "api", ResourceRecord.Create(
                "g1",
                Json("""{"A":{}}"""),
                new Dictionary<ApplicationName, ReadOnlyMemory<byte>> { ["appb"] = Json("""{"B":{"k":{}}}""") },
                Json("""{"A":null}""")), null);

            // Act
            await store.TryWriteAsync("appa", "api", ResourceRecord.Create(
                "g2", default, new Dictionary<ApplicationName, ReadOnlyMemory<byte>>(), default), null);
            ResourceRecord? read = await store.ReadAsync("appa", "api");

            // Assert
            read!.Generation.ShouldBe("g2");
            read.Deployment.IsEmpty.ShouldBeTrue();
            read.Override.IsEmpty.ShouldBeTrue();
            read.Claims.ShouldBeEmpty();
            string directory = store.GetInstanceDirectory("appa", "api");
            Directory.Exists(directory).ShouldBeTrue();
            Directory.GetFiles(directory).ShouldBeEmpty();
            ResourceConfigurationRecord.Load(directory).Digest.ShouldBe(read.Digest);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Records: a generation is a DNS label")]
    [InlineData("")]
    [InlineData("G1")]
    [InlineData("-g1")]
    [InlineData("g_1")]
    public void Create_WithInvalidGeneration_ShouldThrow(string generation)
    {
        // Act & Assert
        Should.Throw<ArgumentException>(() =>
            ResourceRecord.Create(generation, default, new Dictionary<ApplicationName, ReadOnlyMemory<byte>>(), default));
    }

    private static ReadOnlyMemory<byte> Json(string value) => Encoding.UTF8.GetBytes(value);

    private static string CreateDirectory() =>
        Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "records-" + Guid.NewGuid().ToString("N"))).FullName;
}
