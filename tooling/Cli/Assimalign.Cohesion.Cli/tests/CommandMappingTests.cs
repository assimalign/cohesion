using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Cli.Tests;

/// <summary>Tests thin wrapper arguments without spawning dotnet.</summary>
public sealed class CommandMappingTests
{
    /// <summary>Omission preserves the gateway's own defaults.</summary>
    [Fact(DisplayName = "Cohesion Test [Tooling.Cli] - Run: Should omit unspecified gateway and preserve passthrough")]
    public async Task Run_WithoutGateway_ShouldPreserveArgumentsAsync()
    {
        using var fixture = new CliFixture();
        string project = fixture.Gateway();
        fixture.Runner.ExitCode = 17;

        int result = await fixture.Application().ExecuteAsync(
            ["run", "--adopt", "--restart-orphans", "--external", "api=http=http://host", "--realize", "api",
                "--", "--parameter", "name=a b", "--gateway", "unregistered"], CancellationToken.None);

        result.ShouldBe(17);
        fixture.Runner.Calls.Single().Arguments.ShouldBe(new[]
        {
            "run", "--project", project, "--", "--adopt", "--restart-orphans", "--external",
            "api=http=http://host", "--realize", "api", "--parameter", "name=a b", "--gateway", "unregistered"
        });
        fixture.Runner.Calls[0].Executable.ShouldBe("dotnet");
        fixture.Runner.Calls[0].Directory.ShouldBe(Path.GetDirectoryName(project));
    }

    /// <summary>Provider names are the generated gateway's contract.</summary>
    [Fact(DisplayName = "Cohesion Test [Tooling.Cli] - Run: Should forward every explicit gateway name")]
    public async Task Run_WithGateway_ShouldForwardNameAsync()
    {
        using var fixture = new CliFixture();
        string project = fixture.Gateway();

        foreach (string name in new[] { "local", "inprocess", "docker", "kubernetes", "custom" })
        {
            (await fixture.Application().ExecuteAsync(["run", "--gateway", name], CancellationToken.None)).ShouldBe(0);
            fixture.Runner.Calls.Last().Arguments.ShouldBe(new[] { "run", "--project", project, "--", "--gateway", name });
        }
    }

    /// <summary>Later passthrough modes retain the gateway parser's last-value behavior.</summary>
    [Fact(DisplayName = "Cohesion Test [Tooling.Cli] - Deploy: Should map apply before passthrough mode")]
    public async Task Deploy_WithTeardown_ShouldPreserveOrderAsync()
    {
        using var fixture = new CliFixture();
        string project = fixture.Gateway();

        int result = await fixture.Application().ExecuteAsync(
            ["deploy", "--gateway", "kubernetes", "--context", "cluster", "--", "--mode", "teardown"], CancellationToken.None);

        result.ShouldBe(0);
        fixture.Runner.Calls.Single().Arguments.ShouldBe(new[]
        {
            "run", "--project", project, "--", "--gateway", "kubernetes", "--mode", "apply", "--context", "cluster", "--mode", "teardown"
        });
    }

    /// <summary>Publishing accepts a positional project or discovers the gateway.</summary>
    [Fact(DisplayName = "Cohesion Test [Tooling.Cli] - Publish: Should map project and retain MSBuild arguments")]
    public async Task Publish_WithProject_ShouldForwardArgumentsAsync()
    {
        using var fixture = new CliFixture();
        string project = fixture.Gateway();

        (await fixture.Application().ExecuteAsync(["publish", "Api.csproj", "-c", "Release", "--", "-p:Value=a b"], CancellationToken.None)).ShouldBe(0);
        fixture.Runner.Calls.Last().Arguments.ShouldBe(new[] { "publish", "Api.csproj", "-c", "Release", "-p:Value=a b" });
        (await fixture.Application().ExecuteAsync(["publish"], CancellationToken.None)).ShouldBe(0);
        fixture.Runner.Calls.Last().Arguments.ShouldBe(new[] { "publish", project });
        (await fixture.Application().ExecuteAsync(["publish", "--project", "Gateway"], CancellationToken.None)).ShouldBe(0);
        fixture.Runner.Calls.Last().Arguments.ShouldBe(new[] { "publish", Path.Combine(fixture.Root, "Gateway") });
    }

    /// <summary>The container mechanism uses private SDK state and preserves child arguments.</summary>
    [Fact(DisplayName = "Cohesion Test [Tooling.Cli] - Publish: Should forward the in-container target exactly")]
    public async Task Publish_InContainer_ShouldForwardPrivateStateAsync()
    {
        using var fixture = new CliFixture();
        string project = fixture.Gateway();
        fixture.Runner.ExitCode = 19;

        (await fixture.Application().ExecuteAsync(
            ["publish", "--in-container", "--project", project, "-c", "Release", "--", "-p:CohesionContainerRepository=team/api"],
            CancellationToken.None)).ShouldBe(19);

        fixture.Runner.Calls.Single().Arguments.ShouldBe(new[]
        {
            "publish", project, "-t:CohesionPublishImage", "-p:_CohesionImageInContainer=true",
            "-c", "Release", "-p:CohesionContainerRepository=team/api"
        });
        fixture.Runner.Calls[0].Executable.ShouldBe("dotnet");
        fixture.Runner.Calls[0].Directory.ShouldBe(fixture.Root);
    }

    /// <summary>Developer token stdout belongs exclusively to the child process.</summary>
    [Fact(DisplayName = "Cohesion Test [Tooling.Cli] - TrustIssue: Should map developer and leave stdout untouched")]
    public async Task TrustIssue_WithDeveloper_ShouldForwardArgumentsAsync()
    {
        using var fixture = new CliFixture();
        string project = fixture.Gateway();
        fixture.Runner.ExitCode = 23;

        int result = await fixture.Application().ExecuteAsync(
            ["trust", "issue", "--developer", "Dev Name", "--project", "Gateway", "--", "--mode", "trust-issue"], CancellationToken.None);

        result.ShouldBe(23);
        fixture.Runner.Calls.Single().Arguments.ShouldBe(new[]
        {
            "run", "--project", project, "--", "--mode", "trust-issue", "--developer", "Dev Name", "--mode", "trust-issue"
        });
        fixture.Output.ToString().ShouldBeEmpty();
        fixture.Error.ToString().ShouldBeEmpty();
    }

    /// <summary>Grant wrappers preserve local paths and control-plane URLs.</summary>
    [Fact(DisplayName = "Cohesion Test [Tooling.Cli] - TrustAdd: Should map peer and source")]
    public async Task TrustAdd_WithSource_ShouldForwardArgumentsAsync()
    {
        using var fixture = new CliFixture();
        string project = fixture.Gateway();

        foreach (string source in new[] { "../peer export.json", "https://peer.example" })
        {
            (await fixture.Application().ExecuteAsync(["trust", "add", "peer", "--from", source], CancellationToken.None)).ShouldBe(0);
            fixture.Runner.Calls.Last().Arguments.ShouldBe(new[]
            {
                "run", "--project", project, "--", "--mode", "trust-add", "--peer", "peer", "--from", source
            });
        }
    }

    /// <summary>The accepted roster stays equal to the installed template package's source.</summary>
    [Fact(DisplayName = "Cohesion Test [Tooling.Cli] - New: Should forward each shipped template verbatim")]
    public async Task New_WithShippedTemplate_ShouldForwardArgumentsAsync()
    {
        using var fixture = new CliFixture();
        string content = Path.Combine(CliFixture.Repository, "tooling", "templates", "Assimalign.Cohesion.Templates", "src", "content");
        string[] shipped = Directory.GetFiles(content, "template.json", SearchOption.AllDirectories).Select(path =>
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.GetProperty("shortName").GetString()!;
        }).Order(StringComparer.Ordinal).ToArray();
        Templates.Names.Order(StringComparer.Ordinal).ShouldBe(shipped);
        foreach (string name in Templates.Names)
        {
            string spelling = name.ToUpperInvariant();
            (await fixture.Application().ExecuteAsync(
                ["new", spelling, "-n", "Acme", "--applicationName", "acme", "--", "--custom", "a b"], CancellationToken.None)).ShouldBe(0);
            fixture.Runner.Calls.Last().Arguments.ShouldBe(new[]
            {
                "new", spelling, "-n", "Acme", "--applicationName", "acme", "--custom", "a b"
            });
        }
    }

    /// <summary>Topology spelling and placement reach dotnet unchanged.</summary>
    [Fact(DisplayName = "Cohesion Test [Tooling.Cli] - New: Should forward landing-zone topology")]
    public async Task New_WithTopology_ShouldForwardArgumentsAsync()
    {
        using var fixture = new CliFixture();

        foreach (string topology in new[] { "single", "federated" })
        {
            (await fixture.Application().ExecuteAsync(["new", "cohesion-landing-zone", "-n", "Example", "--topology", topology], CancellationToken.None)).ShouldBe(0);
            fixture.Runner.Calls.Last().Arguments.ShouldBe(new[] { "new", "cohesion-landing-zone", "-n", "Example", "--topology", topology });
        }
        (await fixture.Application().ExecuteAsync(["new", "--list"], CancellationToken.None)).ShouldBe(0);
        fixture.Runner.Calls.Last().Arguments.ShouldBe(new[] { "new", "list", "cohesion" });
    }

    /// <summary>Template errors retain the child exit code with one versioned install hint.</summary>
    [Fact(DisplayName = "Cohesion Test [Tooling.Cli] - New: Should emit one pinned install hint on child failure")]
    public async Task New_WhenChildFails_ShouldPrintInstallHintAsync()
    {
        using var fixture = new CliFixture();
        fixture.Runner.ExitCode = 5;

        (await fixture.Application().ExecuteAsync(["new", Templates.Names[0]], CancellationToken.None)).ShouldBe(5);

        fixture.Error.ToString().ShouldBe($"Cohesion templates are not installed. Run: dotnet new install {Templates.PackageId}::{CliApplication.Version}{Environment.NewLine}");
    }

    /// <summary>Deferred functionality fails before any child or network action.</summary>
    [Fact(DisplayName = "Cohesion Test [Tooling.Cli] - Deferred: Should reject unsupported publish and trust options")]
    public async Task Execute_WithDeferredOption_ShouldReturnTwoAsync()
    {
        foreach ((string[] args, string expected) in new[]
        {
            (new[] { "publish", "--in-container=true" }, "does not take a value"),
            (new[] { "trust", "add", "peer", "--against", "provider" }, "#982"),
            (new[] { "trust", "add", "peer", "--allow=secret" }, "O25"),
            (new[] { "new", "unknown" }, string.Join(", ", Templates.Names)),
            (new[] { "new", "cohesion-web", "--topology", "single" }, "cohesion-landing-zone"),
            (new[] { "new", "cohesion-landing-zone", "--topology", "unknown" }, "single or federated")
        })
        {
            using var fixture = new CliFixture();
            (await fixture.Application().ExecuteAsync(args, CancellationToken.None)).ShouldBe(2);
            fixture.Error.ToString().ShouldContain(expected, Case.Sensitive);
            fixture.Runner.Calls.ShouldBeEmpty();
        }
    }

    /// <summary>Deferred single-resource areas are never blindly sent to dotnet.</summary>
    [Fact(DisplayName = "Cohesion Test [Tooling.Cli] - New: Should identify every deferred area")]
    public async Task New_WithDeferredArea_ShouldNameGapAsync()
    {
        using var fixture = new CliFixture();
        foreach (string area in new[] { "apimanager", "emailhub", "eventhub", "iothub", "loadbalancer", "logspace", "mediahub",
            "messagehub", "natgateway", "notificationhub", "scheduler", "vpngateway" })
        {
            (await fixture.Application().ExecuteAsync(["new", "cohesion-" + area], CancellationToken.None)).ShouldBe(2);
        }
        fixture.Error.ToString().Split("No template ships for this area yet").Length.ShouldBe(13);
        fixture.Runner.Calls.ShouldBeEmpty();
    }

    /// <summary>Version removes SDK commit metadata without changing prerelease identifiers.</summary>
    [Fact(DisplayName = "Cohesion Test [Tooling.Cli] - Version: Should match canonical version without commit metadata")]
    public async Task Version_WithBuildMetadata_ShouldStripSuffixAsync()
    {
        using var fixture = new CliFixture();
        string informational = typeof(CliApplication).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;

        (await fixture.Application().ExecuteAsync(["--version"], CancellationToken.None)).ShouldBe(0);

        CliApplication.StripBuildMetadata("10.0.1-preview.3+abc123").ShouldBe("10.0.1-preview.3");
        fixture.Output.ToString().Trim().ShouldBe(informational.Split('+')[0]);
        fixture.Output.ToString().ShouldNotContain("+");
        CliApplication.Version.ShouldBe("10.0.1-preview.3");
    }

    /// <summary>Help and malformed commands never start a process.</summary>
    [Fact(DisplayName = "Cohesion Test [Tooling.Cli] - Help: Should describe verbs and reject missing values")]
    public async Task Execute_WithHelpOrMissingArguments_ShouldAvoidProcessesAsync()
    {
        using var fixture = new CliFixture();
        (await fixture.Application().ExecuteAsync(["--help"], CancellationToken.None)).ShouldBe(0);
        foreach (string verb in new[] { "new", "run", "publish", "deploy", "trust", "parameter", "login", "status" })
        {
            fixture.Output.ToString().ShouldContain(verb, Case.Sensitive);
        }
        foreach (string[] args in new[]
        {
            new[] { "run", "--project" }, new[] { "run", "--gateway" },
            new[] { "trust", "issue" }, new[] { "trust", "add", "peer" },
            new[] { "run", "--gateway", "local", "--gateway", "docker" }, new[] { "unsupported" }
        })
        {
            (await fixture.Application().ExecuteAsync(args, CancellationToken.None)).ShouldBe(2);
        }
        fixture.Runner.Calls.ShouldBeEmpty();
    }
}
