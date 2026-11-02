using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Cli.Internal;

namespace Assimalign.Cohesion.Cli.Tests;

/// <summary>Tests gateway project discovery without MSBuild evaluation.</summary>
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
}
