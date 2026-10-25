using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

using Assimalign.Cohesion.Hosting.Resources;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Hosting.Resources.Tests;

public sealed class ResourceConfigurationRecordTests : IDisposable
{
    private const string DisplayPrefix = "Cohesion Test [Hosting.Resources] - Deployment record: ";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "cohesion-record-" + Guid.NewGuid().ToString("N"));

    public ResourceConfigurationRecordTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
    }

    [Fact(DisplayName = DisplayPrefix + "Load reads the deployment, claim and override layers")]
    public void Load_WithEveryLayer_ReadsThemAll()
    {
        // Arrange
        Write("deployment.json", """{"Test:Policy":{"origins":["https://a.example"]}}""");
        Write("claims.billing.json", """{"Test:Databases":{"ledger":{"engine":"sql"}}}""");
        Write("claims.audit.json", """{"Test:Databases":{"trail":{"engine":"sql"}}}""");
        Write("override.json", """{"Test:Policy":{"maxAgeSeconds":60}}""");
        Write("notes.txt", "ignored");
        Write("claims.Not_A_Label.json", "{}");

        // Act
        ResourceConfigurationRecord record = ResourceConfigurationRecord.Load(_directory);

        // Assert
        record.ClaimOwners.ShouldBe(["audit", "billing"]);
        record.Digest.ShouldStartWith("sha256:");
        record.Digest.Length.ShouldBe("sha256:".Length + 64);
    }

    [Fact(DisplayName = DisplayPrefix + "The digest covers exact bytes, so the writer computes it without a canonical form")]
    public void Load_WithReformattedLayer_ChangesTheDigest()
    {
        // Arrange
        Write("deployment.json", """{"Test:Policy":{"origins":["https://a.example"]}}""");
        string compact = ResourceConfigurationRecord.Load(_directory).Digest;
        Write("deployment.json", """{ "Test:Policy": { "origins": ["https://a.example"] } }""");

        // Act
        string spaced = ResourceConfigurationRecord.Load(_directory).Digest;

        // Assert
        spaced.ShouldNotBe(compact);
        ResourceConfigurationRecord.Load(_directory).Digest.ShouldBe(spaced);
    }

    [Fact(DisplayName = DisplayPrefix + "The digest frames each file by name, so moving bytes between layers changes it")]
    public void Create_SameBytesInDifferentLayers_ProducesDifferentDigests()
    {
        // Arrange
        byte[] layer = Encoding.UTF8.GetBytes("""{"Test:Policy":{"maxAgeSeconds":5}}""");

        // Act
        string asDeployment = ResourceConfigurationRecord.Create(layer, [], null).Digest;
        string asOverride = ResourceConfigurationRecord.Create(null, [], layer).Digest;

        // Assert
        asDeployment.ShouldNotBe(asOverride);
    }

    [Fact(DisplayName = DisplayPrefix + "An empty directory is a record with no layers")]
    public void Load_EmptyDirectory_ReturnsAnEmptyRecord()
    {
        // Act
        ResourceConfigurationRecord record = ResourceConfigurationRecord.Load(_directory);

        // Assert
        record.ClaimOwners.ShouldBeEmpty();
        record.LayersInPrecedenceOrder().ShouldBeEmpty();
    }

    [Theory(DisplayName = DisplayPrefix + "A layer that is not a JSON object is refused")]
    [InlineData("[1,2]")]
    [InlineData("\"text\"")]
    [InlineData("{ not json")]
    public void Load_LayerIsNotAnObject_ShouldThrow(string content)
    {
        // Arrange
        Write("override.json", content);

        // Act
        Should.Throw<InvalidDataException>(() => ResourceConfigurationRecord.Load(_directory));
    }

    [Fact(DisplayName = DisplayPrefix + "A layer larger than a ConfigMap is refused")]
    public void Load_OversizedLayer_ShouldThrow()
    {
        // Arrange
        Write("deployment.json", "{\"Test:Blob\":\"" + new string('x', ResourceConfigurationRecord.MaximumLayerLength) + "\"}");

        // Act
        Should.Throw<InvalidDataException>(() => ResourceConfigurationRecord.Load(_directory));
    }

    [Fact(DisplayName = DisplayPrefix + "A missing directory is reported, not treated as an empty record")]
    public void Load_MissingDirectory_ShouldThrow()
    {
        // Act
        Should.Throw<DirectoryNotFoundException>(() => ResourceConfigurationRecord.Load(Path.Combine(_directory, "absent")));
    }

    [Fact(DisplayName = DisplayPrefix + "Claim owners repeat or break the DNS label grammar only in a malformed record")]
    public void Create_DuplicateOwner_ShouldThrow()
    {
        // Arrange
        byte[] layer = Encoding.UTF8.GetBytes("{}");
        var claims = new List<KeyValuePair<string, byte[]>>
        {
            KeyValuePair.Create("billing", layer),
            KeyValuePair.Create("billing", layer),
        };

        // Act
        Should.Throw<InvalidDataException>(() => ResourceConfigurationRecord.Create(null, claims, null));
    }

    [Fact(DisplayName = DisplayPrefix + "A layer holding an empty object sets nothing, so it counts as absent")]
    public void Load_EmptyObjectLayers_ShouldMatchARecordWithoutThem()
    {
        // Arrange
        Write("deployment.json", """{"Test:Policy":{"origins":["https://a.example"]}}""");
        string without = ResourceConfigurationRecord.Load(_directory).Digest;
        Write("override.json", "{}");
        Write("claims.billing.json", " { } ");

        // Act
        ResourceConfigurationRecord record = ResourceConfigurationRecord.Load(_directory);

        // Assert
        record.Digest.ShouldBe(without);
        record.ClaimOwners.ShouldBeEmpty();
    }

    private void Write(string name, string content) =>
        File.WriteAllText(Path.Combine(_directory, name), content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
}
