using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Cli.Tests;

/// <summary>Tests project markers and state/application resolution without MSBuild evaluation.</summary>
public sealed class GatewayDiscoveryTests
{
    /// <summary>The SDK name part is matched case-insensitively.</summary>
    [Fact(DisplayName = "Cohesion Test [Tooling.Cli] - Discovery: Should accept unversioned and versioned SDK attributes")]
    public void ResolveProject_WithSdkAttribute_ShouldMatchName()
    {
        foreach (string sdk in new[] { "Assimalign.Cohesion.Sdk.Gateway", "Assimalign.Cohesion.Sdk.Gateway/1.2.3", "assimalign.cohesion.sdk.gateway/1.2.3" })
        {
            using var fixture = new CliFixture();
            string project = fixture.Gateway(sdk: sdk);
            fixture.Write("Other.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            fixture.Gateway(path: "Gateway/bin/Ignore.csproj");
            fixture.Gateway(path: "Gateway/obj/Ignore.csproj");

            GatewayDiscovery.ResolveProject(fixture.Root, null).ShouldBe(project);
        }
    }

    /// <summary>MSBuild child SDK declarations are recognized too.</summary>
    [Fact(DisplayName = "Cohesion Test [Tooling.Cli] - Discovery: Should accept a child Sdk element")]
    public void ResolveProject_WithSdkElement_ShouldMatchName()
    {
        using var fixture = new CliFixture();
        string project = fixture.Write("Nested/Gateway.csproj", "<Project><Sdk Name=\"Assimalign.Cohesion.Sdk.Gateway\" Version=\"1.2.3\" /></Project>");

        GatewayDiscovery.ResolveProject(fixture.Root, null).ShouldBe(project);
    }

    /// <summary>Zero and multiple candidates produce actionable diagnostics.</summary>
    [Fact(DisplayName = "Cohesion Test [Tooling.Cli] - Discovery: Should list ambiguous candidates")]
    public void ResolveProject_WithZeroOrMultipleCandidates_ShouldFail()
    {
        using var fixture = new CliFixture();
        fixture.Write("Plain.csproj", "<Project Sdk=\"Assimalign.Cohesion.Sdk.Gateway.Lookalike\" />");

        Should.Throw<CliException>(() => GatewayDiscovery.ResolveProject(fixture.Root, null)).Message.ShouldContain("Candidates: (none)", Case.Sensitive);
        string first = fixture.Gateway();
        string second = fixture.Gateway(path: "Other/Other.csproj");
        string message = Should.Throw<CliException>(() => GatewayDiscovery.ResolveProject(fixture.Root, null)).Message;
        message.ShouldContain(first, Case.Sensitive);
        message.ShouldContain(second, Case.Sensitive);
    }

    /// <summary>An explicit project or directory limits selection.</summary>
    [Fact(DisplayName = "Cohesion Test [Tooling.Cli] - Discovery: Should honor explicit project paths")]
    public async Task Execute_WithProject_ShouldSelectRequestedProjectAsync()
    {
        using var fixture = new CliFixture();
        string first = fixture.Gateway();
        fixture.Gateway(path: "Other/Other.csproj");

        GatewayDiscovery.ResolveProject(fixture.Root, "Gateway").ShouldBe(first);
        GatewayDiscovery.ResolveProject(fixture.Root, "Gateway/Gateway.csproj").ShouldBe(first);
        (await fixture.Application().ExecuteAsync(["run", "--project", "Gateway/Gateway.csproj"], CancellationToken.None)).ShouldBe(0);
        fixture.Runner.Calls[0].Arguments[2].ShouldBe(first);
    }

    /// <summary>Application identity follows explicit selection, project properties, props, then local directories.</summary>
    [Fact(DisplayName = "Cohesion Test [Tooling.Cli] - Application: Should honor the complete precedence chain")]
    public void ResolveApplication_WithProperties_ShouldHonorPrecedence()
    {
        using var fixture = new CliFixture();
        string project = fixture.Gateway("<CohesionApplicationName>primary</CohesionApplicationName><CohesionApplication>alias</CohesionApplication>");
        fixture.Write("Directory.Build.props", "<Project><PropertyGroup><CohesionApplication>parent</CohesionApplication></PropertyGroup></Project>");

        GatewayDiscovery.ResolveApplication(project, fixture.StateRoot, "explicit").ShouldBe("explicit");
        GatewayDiscovery.ResolveApplication(project, fixture.StateRoot, null).ShouldBe("primary");
        fixture.Gateway("<CohesionApplication>alias</CohesionApplication>");
        GatewayDiscovery.ResolveApplication(project, fixture.StateRoot, null).ShouldBe("alias");
        fixture.Gateway("");
        GatewayDiscovery.ResolveApplication(project, fixture.StateRoot, null).ShouldBe("parent");
        fixture.Write("Gateway/Directory.Build.props", "<Project><PropertyGroup><CohesionApplication>nearest</CohesionApplication></PropertyGroup></Project>");
        GatewayDiscovery.ResolveApplication(project, fixture.StateRoot, null).ShouldBe("nearest");
        fixture.Write("Gateway/Directory.Build.props", "<Project />");
        Directory.CreateDirectory(Path.Combine(fixture.StateRoot, "only"));
        GatewayDiscovery.ResolveApplication(project, fixture.StateRoot, null).ShouldBe("only");
        Directory.CreateDirectory(Path.Combine(fixture.StateRoot, "second"));
        Should.Throw<CliException>(() => GatewayDiscovery.ResolveApplication(project, fixture.StateRoot, null)).Message.ShouldContain("--app");
    }

    /// <summary>The default state root belongs to the gateway project, independent of the invoking directory.</summary>
    [Fact(DisplayName = "Cohesion Test [Tooling.Cli] - StateRoot: Should resolve beside gateway or use override")]
    public void GetStateRoot_WithOverride_ShouldUseCallerRelativePath()
    {
        using var fixture = new CliFixture();
        string project = fixture.Gateway();

        GatewayDiscovery.GetStateRoot(project, null, fixture.Root).ShouldBe(fixture.StateRoot);
        GatewayDiscovery.GetStateRoot(project, "custom", fixture.Root).ShouldBe(Path.Combine(fixture.Root, "custom"));
    }

    /// <summary>Names cannot redirect the state readers into private keys or another application.</summary>
    [Fact(DisplayName = "Cohesion Test [Tooling.Cli] - Application: Should reject traversal and unresolved properties")]
    public void ResolveApplication_WithUnsafeName_ShouldFail()
    {
        using var fixture = new CliFixture();
        string project = fixture.Gateway();
        foreach (string name in new[] { "..", "../trust", "x/y", "x\\y", "C:escape", "$(Application)" })
        {
            Should.Throw<CliException>(() => GatewayDiscovery.ResolveApplication(project, fixture.StateRoot, name));
        }
    }
}
