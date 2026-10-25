using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting.Resources;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Hosting.Resources.Tests;

public class ResourceRuntimeSurfaceTests
{
    private const string DisplayPrefix = "Cohesion Test [Hosting.Resources] - Runtime surface: ";

    [Fact(DisplayName = DisplayPrefix + "The exported document lists sections, actions and tools without values")]
    public void Export_WithEveryMember_WritesTheRuntimeDocument()
    {
        // Arrange
        ResourceControlPlane plane = CreatePlane(out _);

        // Act
        using JsonDocument document = JsonDocument.Parse(plane.Surface.Export());

        // Assert
        JsonElement root = document.RootElement;
        root.GetProperty("schema").GetString().ShouldBe(ResourceRuntimeSurface.DocumentSchema);
        JsonElement section = root.GetProperty("configuration")[0];
        section.GetProperty("path").GetString().ShouldBe("Test:Policy");
        section.GetProperty("mutability").GetString().ShouldBe("live");
        section.GetProperty("scope").GetString().ShouldBe("process");
        section.GetProperty("claimable").GetBoolean().ShouldBeFalse();
        section.GetProperty("schema").GetProperty("type").ValueKind.ShouldNotBe(JsonValueKind.Undefined);
        JsonElement action = root.GetProperty("actions")[0];
        action.GetProperty("name").GetString().ShouldBe("flush-cache");
        action.GetProperty("target").GetString().ShouldBe("everyInstance");
        JsonElement tool = root.GetProperty("tools")[0];
        tool.GetProperty("name").GetString().ShouldBe("sql");
        tool.GetProperty("protocol").GetString().ShouldBe("websocket");
        tool.GetProperty("subprotocol").GetString().ShouldBe("cohesion-sql.v1");
        tool.GetProperty("requiresDataAccess").GetBoolean().ShouldBeTrue();
        Encoding.UTF8.GetString(plane.Surface.Export()).ShouldNotContain("https://a.example", Case.Sensitive);
    }

    [Fact(DisplayName = DisplayPrefix + "The digest is stable for the same surface")]
    public void Digest_SameSurface_IsStable()
    {
        // Act
        string first = CreatePlane(out _).Surface.Digest;
        string second = CreatePlane(out _).Surface.Digest;

        // Assert
        first.ShouldBe(second);
        first.ShouldStartWith("sha256:");
    }

    [Fact(DisplayName = DisplayPrefix + "An action binds its JSON input and returns its JSON result")]
    public async Task InvokeActionAsync_WithInput_ReturnsTheResult()
    {
        // Arrange
        ResourceControlPlane plane = CreatePlane(out List<string?> prefixes);

        // Act
        ReadOnlyMemory<byte> result = await plane.InvokeActionAsync("flush-cache", Encoding.UTF8.GetBytes("""{"prefix":"orders"}"""), CancellationToken.None);

        // Assert
        prefixes.ShouldBe(["orders"]);
        using JsonDocument document = JsonDocument.Parse(result);
        document.RootElement.GetProperty("flushed").GetInt32().ShouldBe(3);
    }

    [Fact(DisplayName = DisplayPrefix + "An unknown action and an unbindable input are refused")]
    public async Task InvokeActionAsync_UnknownOrInvalid_ShouldThrow()
    {
        // Arrange
        ResourceControlPlane plane = CreatePlane(out _);

        // Act
        await Should.ThrowAsync<NotSupportedException>(() =>
            plane.InvokeActionAsync("rebuild-index", Encoding.UTF8.GetBytes("{}"), CancellationToken.None).AsTask());
        await Should.ThrowAsync<ArgumentException>(() =>
            plane.InvokeActionAsync("flush-cache", Encoding.UTF8.GetBytes("""{"prefix":"orders","unknown":1}"""), CancellationToken.None).AsTask());
    }

    [Fact(DisplayName = DisplayPrefix + "A WebSocket subprotocol is an HTTP token and only for WebSocket tools")]
    public void CreateTool_InvalidSubprotocol_ShouldThrow()
    {
        // Act
        Should.Throw<ArgumentException>(() => ResourceTool.Create("sql", "admin", ResourceToolProtocol.WebSocket, "cohesion sql"));
        Should.Throw<ArgumentException>(() => ResourceTool.Create("sql", "admin", ResourceToolProtocol.Http, "cohesion-sql.v1"));
    }

    [Fact(DisplayName = DisplayPrefix + "Describe writes the frozen surface where the SDK asked")]
    public void TryDescribe_WithDescribePath_WritesTheSurface()
    {
        // Arrange
        string path = Path.Combine(Path.GetTempPath(), "cohesion-describe-" + Guid.NewGuid().ToString("N"), "resource.runtime.json");
        ResourceContext context = ResourceContext.FromEnvironment(new Dictionary<string, string?>
        {
            [AppEnvironment.Variables.DescribePath] = path,
        });
        ResourceControlPlane plane = CreatePlane(out _);
        plane.AttachHost(new TestHost(new TestHostOptions()));

        try
        {
            // Act
            bool described = ResourceRuntime.TryDescribe(context, plane);

            // Assert
            described.ShouldBeTrue();
            File.ReadAllBytes(path).ShouldBe(plane.Surface.Export());
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact(DisplayName = DisplayPrefix + "Without a describe path the host runs normally")]
    public void TryDescribe_WithoutDescribePath_ReturnsFalse()
    {
        // Act
        bool described = ResourceRuntime.TryDescribe(new ResourceContext(), CreatePlane(out _));

        // Assert
        described.ShouldBeFalse();
    }

    private static ResourceControlPlane CreatePlane(out List<string?> prefixes)
    {
        var flushed = new List<string?>();
        prefixes = flushed;
        ResourceControlPlane plane = ResourceControlPlane.Create();
        plane.AddConfiguration(ResourceConfigurationSection.Create(
            "Test:Policy",
            TestSurfaceJsonContext.Default.TestPolicyOptions,
            ResourceConfigurationMutability.Live,
            new TestPolicyOptions { Origins = ["https://a.example"] },
            static _ => null));
        plane.AddAction(ResourceAction.Create(
            "flush-cache",
            TestSurfaceJsonContext.Default.TestFlushInput,
            TestSurfaceJsonContext.Default.TestFlushResult,
            ResourceActionTarget.EveryInstance,
            (input, _) =>
            {
                flushed.Add(input.Prefix);
                return ValueTask.FromResult(new TestFlushResult { Flushed = 3 });
            }));
        plane.AddTool(ResourceTool.Create("sql", "admin", ResourceToolProtocol.WebSocket, "cohesion-sql.v1"));
        return plane;
    }
}
