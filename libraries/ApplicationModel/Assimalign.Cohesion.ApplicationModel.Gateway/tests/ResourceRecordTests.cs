using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Tests;

public sealed class ResourceRecordTests
{
    private const string Surface = """
        {"schema":"cohesion/runtime/v1","configuration":[
        {"path":"Web:Cors","mutability":"live","scope":"process","claimable":true,"schema":{"type":["object","null"]}},
        {"path":"Orders:Limits","mutability":"restart","scope":"process","claimable":true,"schema":{"type":["object","null"]}}],
        "actions":[],"tools":[]}
        """;

    private static readonly IReadOnlyDictionary<ApplicationName, ReadOnlyMemory<byte>> _noClaims =
        new Dictionary<ApplicationName, ReadOnlyMemory<byte>>();

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Records: a platform override changes the version but never the digest an instance reports")]
    public void Create_WithPlatformOverride_ShouldChangeVersionButNotDigest()
    {
        // Arrange
        ResourceRecord plain = ResourceRecord.Create("g1", Json("""{"Web:Cors":{}}"""), _noClaims, default);

        // Act
        ResourceRecord overridden = ResourceRecord.Create(
            "g1",
            Json("""{"Web:Cors":{}}"""),
            _noClaims,
            default,
            new Dictionary<ResourceExtensionKey, ReadOnlyMemory<byte>> { ["kubernetes/hpa"] = Json("""{"maximum":5}""") });

        // Assert
        overridden.Digest.ShouldBe(plain.Digest);
        overridden.Version.ShouldNotBe(plain.Version);
        overridden.PlatformOverrides.Keys.ShouldBe([(ResourceExtensionKey)"kubernetes/hpa"]);
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Records: a record without platform overrides keeps its earlier version")]
    public void Create_WithoutPlatformOverrides_ShouldMatchTheFourLayerOverload()
    {
        // Arrange
        ResourceRecord fourLayers = ResourceRecord.Create("g1", Json("""{"A":{"b":1}}"""), _noClaims, Json("""{"A":{"b":2}}"""));

        // Act
        ResourceRecord empty = ResourceRecord.Create(
            "g1", Json("""{"A":{"b":1}}"""), _noClaims, Json("""{"A":{"b":2}}"""), new Dictionary<ResourceExtensionKey, ReadOnlyMemory<byte>>());

        // Assert
        empty.Version.ShouldBe(fourLayers.Version);
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Records: a platform override must be JSON")]
    public void Create_WithMalformedPlatformOverride_ShouldThrow()
    {
        // Act
        Action create = () => ResourceRecord.Create(
            "g1",
            default,
            _noClaims,
            default,
            new Dictionary<ResourceExtensionKey, ReadOnlyMemory<byte>> { ["kubernetes/hpa"] = Json("{not json") });

        // Assert
        create.ShouldThrow<InvalidDataException>();
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Records: the restart digest covers overrides and claims of restart sections only")]
    public void GetRestartDigest_ShouldTrackOnlyRestartSections()
    {
        // Arrange
        ResourceSurface surface = ResourceSurface.Parse(Surface);
        ResourceRecord none = ResourceRecord.Create("g1", default, _noClaims, default);
        ResourceRecord live = ResourceRecord.Create("g1", default, _noClaims, Json("""{"Web:Cors":{"origin":"a"}}"""));
        ResourceRecord restart = ResourceRecord.Create("g1", default, _noClaims, Json("""{"Orders:Limits":{"maxItems":4}}"""));
        ResourceRecord changed = ResourceRecord.Create("g1", default, _noClaims, Json("""{"Orders:Limits":{"maxItems":5}}"""));
        ResourceRecord claimed = ResourceRecord.Create(
            "g1",
            default,
            new Dictionary<ApplicationName, ReadOnlyMemory<byte>> { ["appb"] = Json("""{"Orders:Limits":{"maxItems":4}}""") },
            default);

        // Act
        string? noneDigest = none.GetRestartDigest(surface);
        string? liveDigest = live.GetRestartDigest(surface);
        string? restartDigest = restart.GetRestartDigest(surface);

        // Assert
        noneDigest.ShouldBeNull();
        liveDigest.ShouldBeNull();
        restartDigest.ShouldNotBeNull();
        changed.GetRestartDigest(surface).ShouldNotBe(restartDigest);
        claimed.GetRestartDigest(surface).ShouldNotBeNull();
        claimed.GetRestartDigest(surface).ShouldNotBe(restartDigest);
        restart.GetRestartDigest(null).ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Records: platform overrides round-trip through the file store and stay out of the instance directory")]
    public async Task FileResourceRecordStore_PlatformOverrides_ShouldRoundTripOutsideTheInstanceDirectory()
    {
        // Arrange
        using var directory = new TemporaryTestDirectory();
        FileResourceRecordStore store = FileResourceRecordStore.Create(directory.Path);
        ResourceRecord record = ResourceRecord.Create(
            "g1",
            Json("""{"Web:Cors":{}}"""),
            _noClaims,
            default,
            new Dictionary<ResourceExtensionKey, ReadOnlyMemory<byte>> { ["kubernetes/hpa"] = Json("""{"maximum":5}""") });

        // Act
        (await store.TryWriteAsync("appa", "api", record, null)).ShouldBeTrue();
        ResourceRecord? read = await store.ReadAsync("appa", "api");

        // Assert
        read.ShouldNotBeNull();
        read.Version.ShouldBe(record.Version);
        Encoding.UTF8.GetString(read.PlatformOverrides["kubernetes/hpa"].Span).ShouldContain("\"maximum\"", Case.Sensitive);
        Directory.GetFiles(store.GetInstanceDirectory("appa", "api")).Length.ShouldBe(1);
    }

    private static ReadOnlyMemory<byte> Json(string value) => Encoding.UTF8.GetBytes(value);
}
