using System;
using System.IO;
using System.Text;
using System.Text.Json;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.ApplicationModel.Tests;

public sealed class ResourceSurfaceTests
{
    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Surface: Parse exposes sections, actions and tools in declaration order")]
    public void Parse_WithFullDocument_ShouldExposeEverySurfaceEntry()
    {
        // Act
        ResourceSurface surface = TestSurfaceFactory.Create();

        // Assert
        surface.Configuration.Count.ShouldBe(3);
        surface.Configuration[0].Path.ShouldBe((ResourceSectionPath)"Web:Cors");
        surface.Configuration[0].Mutability.ShouldBe(ResourceSurfaceMutability.Live);
        surface.Configuration[0].IsOverridable.ShouldBeTrue();
        surface.Configuration[1].Mutability.ShouldBe(ResourceSurfaceMutability.Restart);
        surface.Configuration[2].Scope.ShouldBe(ResourceSurfaceScope.Backend);
        surface.Configuration[2].Claimable.ShouldBeTrue();
        surface.Configuration[2].IsOverridable.ShouldBeFalse();
        surface.TryGetAction("flush-cache", out ResourceSurfaceAction? action).ShouldBeTrue();
        action!.Target.ShouldBe(ResourceSurfaceActionTarget.EveryInstance);
        surface.Tools.ShouldHaveSingleItem().Subprotocol.ShouldBe("cohesion.sql");
        surface.Digest.ShouldStartWith("sha256:");
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Surface: the digest covers the compact form, so reformatting keeps it")]
    public void Parse_WithIndentedDocument_ShouldReportTheCompactDigest()
    {
        // Arrange
        ResourceSurface compact = TestSurfaceFactory.Create();
        using JsonDocument document = JsonDocument.Parse(compact.Export());
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            document.RootElement.WriteTo(writer);
        }

        string indented = Encoding.UTF8.GetString(buffer.ToArray());

        // Act
        ResourceSurface reparsed = ResourceSurface.Parse(indented);

        // Assert
        indented.ShouldNotBe(Encoding.UTF8.GetString(compact.Export()));
        reparsed.Digest.ShouldBe(compact.Digest);
        reparsed.Export().ShouldBe(compact.Export());
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Surface: members a newer resource adds are ignored")]
    public void Parse_WithUnknownMembers_ShouldIgnoreThem()
    {
        // Act
        ResourceSurface surface = ResourceSurface.Parse(
            """{"schema":"cohesion/runtime/v1","ui":{"group":"web"},"configuration":[{"path":"A","mutability":"live","scope":"process","claimable":false,"schema":true,"order":3}]}""");

        // Assert
        surface.Configuration.ShouldHaveSingleItem().Path.ShouldBe((ResourceSectionPath)"A");
        surface.Actions.ShouldBeEmpty();
        surface.Tools.ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [ApplicationModel] - Surface: Parse rejects documents that break the contract")]
    [InlineData("""{"schema":"cohesion/runtime/v2"}""")]
    [InlineData("""{"configuration":[]}""")]
    [InlineData("""{"schema":"cohesion/runtime/v1","configuration":[{"path":"bad path","mutability":"live","scope":"process","claimable":false,"schema":true}]}""")]
    [InlineData("""{"schema":"cohesion/runtime/v1","configuration":[{"path":"A","mutability":"sometimes","scope":"process","claimable":false,"schema":true}]}""")]
    [InlineData("""{"schema":"cohesion/runtime/v1","configuration":[{"path":"A","mutability":"live","scope":"process","claimable":false,"schema":true},{"path":"A","mutability":"live","scope":"process","claimable":false,"schema":true}]}""")]
    [InlineData("""{"schema":"cohesion/runtime/v1","actions":[{"name":"Flush","target":"anyInstance","input":true,"result":true}]}""")]
    [InlineData("""{"schema":"cohesion/runtime/v1","tools":[{"name":"sql","endpoint":"db","protocol":"http","subprotocol":"x","requiresDataAccess":true}]}""")]
    [InlineData("""[]""")]
    public void Parse_WithContractViolation_ShouldThrowInvalidData(string json)
    {
        // Act & Assert
        Should.Throw<InvalidDataException>(() => ResourceSurface.Parse(json));
    }

    [Theory(DisplayName = "Cohesion Test [ApplicationModel] - Surface: patches that bind are accepted")]
    [InlineData("Web:Cors", """{"defaultPolicy":{"origins":["https://portal.example.com"]}}""")]
    [InlineData("Web:Cors", """{"defaultPolicy":{"maxAgeSeconds":600,"origins":null}}""")]
    [InlineData("Web:Cors", """{"policies":{"partners":{"origins":["https://a.example.com"]}}}""")]
    [InlineData("Web:Cors", """null""")]
    [InlineData("Orders:Limits", """{"mode":"safe"}""")]
    [InlineData("Orders:Limits", """{"ratio":0.5,"mode":null}""")]
    [InlineData("Orders:Schema", """{"invoices":{"columns":4}}""")]
    public void TryValidatePatch_WithBindingPatch_ShouldAccept(string path, string patch)
    {
        // Arrange
        TestSurfaceFactory.Create().TryGetSection(path, out ResourceSurfaceSection? section).ShouldBeTrue();
        using JsonDocument document = JsonDocument.Parse(patch);

        // Act
        bool valid = section!.TryValidatePatch(document.RootElement, out string? error);

        // Assert
        error.ShouldBeNull();
        valid.ShouldBeTrue();
    }

    [Theory(DisplayName = "Cohesion Test [ApplicationModel] - Surface: patches the resource would drop or refuse fail with the path named")]
    [InlineData("Web:Cors", """{"defaultPolicy":{"orgins":["x"]}}""", "$.defaultPolicy.orgins")]
    [InlineData("Web:Cors", """{"defaultPolicy":{"origins":"https://x"}}""", "$.defaultPolicy.origins")]
    [InlineData("Web:Cors", """{"defaultPolicy":{"origins":[1]}}""", "$.defaultPolicy.origins[0]")]
    [InlineData("Web:Cors", """{"defaultPolicy":{"maxAgeSeconds":1.5}}""", "$.defaultPolicy.maxAgeSeconds")]
    [InlineData("Web:Cors", """{"policies":{"partners":{"orgins":[]}}}""", "$.policies.partners.orgins")]
    [InlineData("Web:Cors", """["https://x"]""", "$")]
    [InlineData("Orders:Limits", """{"maxItems":null}""", "$.maxItems")]
    [InlineData("Orders:Limits", """{"mode":"slow"}""", "$.mode")]
    [InlineData("Orders:Schema", """{"invoices":{"columns":"four"}}""", "$.invoices.columns")]
    public void TryValidatePatch_WithInvalidPatch_ShouldReportThePath(string path, string patch, string location)
    {
        // Arrange
        TestSurfaceFactory.Create().TryGetSection(path, out ResourceSurfaceSection? section).ShouldBeTrue();
        using JsonDocument document = JsonDocument.Parse(patch);

        // Act
        bool valid = section!.TryValidatePatch(document.RootElement, out string? error);

        // Assert
        valid.ShouldBeFalse();
        error.ShouldNotBeNull().ShouldContain("'" + location + "'", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Surface: a replacing value is checked whole, so a required member must be present")]
    public void TryValidatePatch_WithReplacingNestedValue_ShouldCheckRequiredMembers()
    {
        // Arrange
        ResourceSurface surface = ResourceSurface.Parse(
            """{"schema":"cohesion/runtime/v1","configuration":[{"path":"A","mutability":"live","scope":"process","claimable":false,"schema":{"type":"object","properties":{"items":{"type":"array","items":{"type":"object","properties":{"id":{"type":"string"}},"required":["id"]}}}}}]}""");
        surface.TryGetSection("A", out ResourceSurfaceSection? section).ShouldBeTrue();
        using JsonDocument missing = JsonDocument.Parse("""{"items":[{}]}""");
        using JsonDocument present = JsonDocument.Parse("""{"items":[{"id":"a"}]}""");

        // Act & Assert
        section!.TryValidatePatch(missing.RootElement, out string? error).ShouldBeFalse();
        error.ShouldNotBeNull().ShouldContain("'id'", Case.Sensitive);
        section.TryValidatePatch(present.RootElement, out _).ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Surface: a recursive schema validates through its self reference")]
    public void TryValidatePatch_WithRecursiveSchema_ShouldFollowTheReference()
    {
        // Arrange
        ResourceSurface surface = ResourceSurface.Parse(
            """{"schema":"cohesion/runtime/v1","configuration":[{"path":"Tree","mutability":"live","scope":"process","claimable":false,"schema":{"type":["object","null"],"properties":{"name":{"type":"string"},"children":{"type":["array","null"],"items":{"$ref":"#"}}}}}]}""");
        surface.TryGetSection("Tree", out ResourceSurfaceSection? section).ShouldBeTrue();
        using JsonDocument valid = JsonDocument.Parse("""{"children":[{"name":"a","children":[{"name":"b"}]}]}""");
        using JsonDocument invalid = JsonDocument.Parse("""{"children":[{"name":"a","children":[{"name":2}]}]}""");

        // Act & Assert
        section!.TryValidatePatch(valid.RootElement, out _).ShouldBeTrue();
        section.TryValidatePatch(invalid.RootElement, out string? error).ShouldBeFalse();
        error.ShouldNotBeNull().ShouldContain("'$.children[0].children[0].name'", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Surface: an action input is validated as a complete value")]
    public void TryValidateInput_WithWrongType_ShouldFail()
    {
        // Arrange
        TestSurfaceFactory.Create().TryGetAction("flush-cache", out ResourceSurfaceAction? action).ShouldBeTrue();
        using JsonDocument valid = JsonDocument.Parse("""{"region":"eu"}""");
        using JsonDocument invalid = JsonDocument.Parse("""{"region":5}""");

        // Act & Assert
        action!.TryValidateInput(valid.RootElement, out _).ShouldBeTrue();
        action.TryValidateInput(invalid.RootElement, out string? error).ShouldBeFalse();
        error.ShouldNotBeNull().ShouldContain("'$.region'", Case.Sensitive);
    }
}
