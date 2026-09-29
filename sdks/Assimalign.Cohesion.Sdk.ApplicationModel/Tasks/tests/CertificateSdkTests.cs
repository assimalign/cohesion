using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Sdk.ApplicationModel.Tests;

public sealed class CertificateSdkTests
{
    [Fact(DisplayName = "Cohesion Test [Sdk] - Certificate: Composite lifting preserves the reserved public literal")]
    public async Task Build_CompositePublicCertificate_ShouldPreserveReservedLiteral()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using ConsumerWorkspace workspace = ConsumerWorkspace.Create("MinimalResource", "EnabledDependency");
        string child = workspace.ProjectFile("EnabledDependency");
        File.WriteAllText(child, File.ReadAllText(child).Replace("</Project>",
            "<ItemGroup><CohesionEndpoint Update=\"admin\" Scheme=\"https\" Certificate=\"public\" /></ItemGroup></Project>", StringComparison.Ordinal));
        string parent = workspace.ProjectFile("MinimalResource");
        File.WriteAllText(parent, File.ReadAllText(parent).Replace("</Project>",
            "<PropertyGroup><CohesionApplication>inventory</CohesionApplication><CohesionResourceKind>Composite</CohesionResourceKind></PropertyGroup>" +
            "<ItemGroup><CohesionResourceReference Include=\"../EnabledDependency/EnabledDependency.csproj\" /></ItemGroup></Project>", StringComparison.Ordinal));

        DotNetBuildResult result = await workspace.BuildAsync("MinimalResource", timeout.Token);
        result.ExitCode.ShouldBe(0, result.Output);
        string manifestPath = ManifestPath(workspace);
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        JsonElement endpoint = manifest.RootElement.GetProperty("endpoints").EnumerateArray()
            .Single(endpoint => endpoint.GetProperty("name").GetString()!.EndsWith("-admin", StringComparison.Ordinal));
        endpoint.GetProperty("certificate").GetString().ShouldBe("public");
        manifest.RootElement.GetProperty("mounts").EnumerateArray()
            .ShouldNotContain(mount => mount.GetProperty("name").GetString()!.EndsWith("-public", StringComparison.Ordinal));
    }

    [Theory(DisplayName = "Cohesion Test [Sdk] - Certificate: HTTPS defaults and explicit Secret mounts round-trip")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Build_HttpsCertificate_ShouldGenerateMountAndRegistration(bool explicitMount)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using ConsumerWorkspace workspace = ConsumerWorkspace.Create("MinimalResource");
        string metadata = explicitMount ? " Certificate=\"tls\"" : string.Empty;
        string mount = explicitMount ? "<CohesionMount Include=\"tls\" Kind=\"Secret\" ContainerPath=\"/cohesion/mounts/tls\" />" : string.Empty;
        AddItems(workspace, $"<CohesionEndpoint Update=\"http\" Scheme=\"https\"{metadata} />{mount}");
        DotNetBuildResult result = await workspace.BuildAsync("MinimalResource", timeout.Token);
        result.ExitCode.ShouldBe(0, result.Output);
        string manifestPath = ManifestPath(workspace);
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        document.RootElement.GetProperty("endpoints")[0].GetProperty("certificate").GetString().ShouldBe("tls");
        JsonElement tls = document.RootElement.GetProperty("mounts").EnumerateArray().Single(mount => mount.GetProperty("name").GetString() == "tls");
        tls.GetProperty("kind").GetString().ShouldBe("Secret");
        File.ReadAllText(Path.Combine(Path.GetDirectoryName(manifestPath)!, "Resource.g.cs")).ShouldContain("RegisterEndpointCertificates");
    }

    [Theory(DisplayName = "Cohesion Test [Sdk] - Certificate: COHSDK010 rejects inconsistent metadata and exempts public")]
    [InlineData("https", "missing", "", false)]
    [InlineData("https", "tls", "Configuration", false)]
    [InlineData("http", "tls", "Secret", false)]
    [InlineData("https", "public", "", true)]
    [InlineData("http", "public", "", true)]
    public async Task Build_CertificateContract_ShouldDiagnoseInvalidMetadata(string scheme, string certificate, string kind, bool valid)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using ConsumerWorkspace workspace = ConsumerWorkspace.Create("MinimalResource");
        string mount = kind.Length == 0 ? string.Empty : $"<CohesionMount Include=\"tls\" Kind=\"{kind}\" />";
        AddItems(workspace, $"<CohesionEndpoint Update=\"http\" Scheme=\"{scheme}\" Certificate=\"{certificate}\" />{mount}");
        DotNetBuildResult result = await workspace.BuildAsync("MinimalResource", timeout.Token);
        if (valid)
        {
            result.ExitCode.ShouldBe(0, result.Output);
            result.Output.ShouldNotContain("COHSDK010");
        }
        else
        {
            result.ExitCode.ShouldNotBe(0, result.Output);
            result.Output.ShouldContain("COHSDK010");
        }
    }

    private static string ManifestPath(ConsumerWorkspace workspace) => Path.Combine(
        workspace.ProjectDirectory("MinimalResource"), "obj", "Debug", "net10.0", RuntimeInformation.RuntimeIdentifier, "cohesion", "resource.json");

    private static void AddItems(ConsumerWorkspace workspace, string items)
    {
        string path = workspace.ProjectFile("MinimalResource");
        File.WriteAllText(path, File.ReadAllText(path).Replace("</Project>", $"<ItemGroup>{items}</ItemGroup></Project>", StringComparison.Ordinal));
    }
}
