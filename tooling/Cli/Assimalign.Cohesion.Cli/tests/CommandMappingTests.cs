using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Cli.Internal;

namespace Assimalign.Cohesion.Cli.Tests;

/// <summary>Tests thin wrapper arguments without spawning dotnet.</summary>
public sealed class CommandMappingTests
{
    /// <summary>Run starts the discovered gateway and hands it every argument unchanged.</summary>
    /// <returns>A task representing command mapping verification.</returns>
    [Fact(DisplayName = "Cohesion Test [Tooling.Cli] - Run: Should run the discovered gateway and preserve passthrough")]
    public async Task Run_WithArguments_ShouldPreserveArgumentsAsync()
    {
        // Arrange
        using var fixture = new CliFixture();
        string project = fixture.Gateway();
        fixture.Runner.ExitCode = 17;

        // Act
        int result = await fixture.Application().ExecuteAsync(
            ["run", "--verbose", "--", "--parameter", "name=a b", "--gateway", "unregistered"], CancellationToken.None);

        // Assert
        result.ShouldBe(17);
        fixture.Runner.Calls.Single().Arguments.ShouldBe(new[]
        {
            "run", "--project", project, "--", "--verbose", "--parameter", "name=a b", "--gateway", "unregistered"
        });
        fixture.Runner.Calls[0].Executable.ShouldBe("dotnet");
        fixture.Runner.Calls[0].Directory.ShouldBe(Path.GetDirectoryName(project));
    }

    /// <summary>Unset and whitespace-only shell variables keep the gateway's launch profile.</summary>
    /// <param name="cohesionEnvironment">The Cohesion shell environment value.</param>
    /// <param name="dotnetEnvironment">The .NET shell environment value.</param>
    /// <returns>A task representing command mapping verification.</returns>
    [Theory(DisplayName = "Cohesion Test [Tooling.Cli] - Run: Should keep the launch profile for unset shell environments")]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData(" \t", null)]
    [InlineData(null, "\r\n ")]
    public async Task Run_WithoutEnvironment_ShouldKeepLaunchProfileAsync(string? cohesionEnvironment, string? dotnetEnvironment)
    {
        // Arrange
        using var fixture = new CliFixture();
        string project = fixture.Gateway();
        CliApplication application = fixture.Application(environment: name =>
            name == "COHESION_ENVIRONMENT" ? cohesionEnvironment : name == "DOTNET_ENVIRONMENT" ? dotnetEnvironment : null);

        // Act
        int result = await application.ExecuteAsync(["run"], CancellationToken.None);

        // Assert
        result.ShouldBe(0);
        fixture.Runner.Calls.Single().Arguments.ShouldBe(["run", "--project", project, "--"]);
    }

    /// <summary>Either explicit shell variable must survive a launch profile.</summary>
    /// <param name="cohesionEnvironment">The Cohesion shell environment value.</param>
    /// <param name="dotnetEnvironment">The .NET shell environment value.</param>
    /// <returns>A task representing command mapping verification.</returns>
    [Theory(DisplayName = "Cohesion Test [Tooling.Cli] - Run: Should preserve explicit shell environments")]
    [InlineData("Development", null)]
    [InlineData(null, "Development")]
    [InlineData(" \t", "Development")]
    [InlineData("Local", null)]
    public async Task Run_WithEnvironmentVariable_ShouldDisableLaunchProfileAsync(string? cohesionEnvironment, string? dotnetEnvironment)
    {
        // Arrange
        using var fixture = new CliFixture();
        string project = fixture.Gateway();
        CliApplication application = fixture.Application(environment: name =>
            name == "COHESION_ENVIRONMENT" ? cohesionEnvironment : name == "DOTNET_ENVIRONMENT" ? dotnetEnvironment : null);

        // Act
        int result = await application.ExecuteAsync(["run", "--", "--verbose"], CancellationToken.None);

        // Assert
        result.ShouldBe(0);
        fixture.Runner.Calls.Single().Arguments.ShouldBe(["run", "--project", project, "--no-launch-profile", "--", "--verbose"]);
    }

    /// <summary>Publishing accepts a positional project or discovers the gateway.</summary>
    /// <returns>A task representing command mapping verification.</returns>
    [Fact(DisplayName = "Cohesion Test [Tooling.Cli] - Publish: Should map project and retain MSBuild arguments")]
    public async Task Publish_WithProject_ShouldForwardArgumentsAsync()
    {
        // Arrange
        using var fixture = new CliFixture();
        string project = fixture.Gateway();

        // Act and assert
        (await fixture.Application().ExecuteAsync(["publish", "Api.csproj", "-c", "Release", "--", "-p:Value=a b"], CancellationToken.None)).ShouldBe(0);
        fixture.Runner.Calls.Last().Arguments.ShouldBe(new[] { "publish", "Api.csproj", "-c", "Release", "-p:Value=a b" });
        (await fixture.Application().ExecuteAsync(["publish"], CancellationToken.None)).ShouldBe(0);
        fixture.Runner.Calls.Last().Arguments.ShouldBe(new[] { "publish", project });
        (await fixture.Application().ExecuteAsync(["publish", "--project", "Gateway"], CancellationToken.None)).ShouldBe(0);
        fixture.Runner.Calls.Last().Arguments.ShouldBe(new[] { "publish", Path.Combine(fixture.Root, "Gateway") });
    }

    /// <summary>The accepted roster stays equal to the installed template package's source.</summary>
    /// <returns>A task representing command mapping verification.</returns>
    [Fact(DisplayName = "Cohesion Test [Tooling.Cli] - New: Should forward each shipped template verbatim")]
    public async Task New_WithShippedTemplate_ShouldForwardArgumentsAsync()
    {
        // Arrange
        using var fixture = new CliFixture();
        string content = Path.Combine(CliFixture.Repository, "tooling", "templates", "Assimalign.Cohesion.Templates", "src", "content");
        string[] shipped = Directory.GetFiles(content, "template.json", SearchOption.AllDirectories).Select(path =>
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.GetProperty("shortName").GetString()!;
        }).Order(StringComparer.Ordinal).ToArray();

        // Act and assert
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

        (await fixture.Application().ExecuteAsync(["new", "--list"], CancellationToken.None)).ShouldBe(0);
        fixture.Runner.Calls.Last().Arguments.ShouldBe(new[] { "new", "list", "cohesion" });
    }

    /// <summary>Template errors retain the child exit code with one versioned install hint.</summary>
    /// <returns>A task representing command mapping verification.</returns>
    [Fact(DisplayName = "Cohesion Test [Tooling.Cli] - New: Should emit one pinned install hint on child failure")]
    public async Task New_WhenChildFails_ShouldPrintInstallHintAsync()
    {
        // Arrange
        using var fixture = new CliFixture();
        fixture.Runner.ExitCode = 5;

        // Act and assert
        (await fixture.Application().ExecuteAsync(["new", Templates.Names[0]], CancellationToken.None)).ShouldBe(5);
        fixture.Error.ToString().ShouldBe($"Cohesion templates are not installed. Run: dotnet new install {Templates.PackageId}::{CliApplication.Version}{Environment.NewLine}");
    }

    /// <summary>Unknown templates and retired commands fail before any child process starts.</summary>
    /// <returns>A task representing command mapping verification.</returns>
    [Fact(DisplayName = "Cohesion Test [Tooling.Cli] - Unsupported: Should reject unknown templates and retired commands")]
    public async Task Execute_WithUnsupportedInput_ShouldReturnTwoAsync()
    {
        foreach ((string[] args, string expected) in new[]
        {
            (new[] { "new", "unknown" }, string.Join(", ", Templates.Names)),
            (new[] { "new", "cohesion-landing-zone" }, "Unknown Cohesion template"),
            (new[] { "deploy" }, "Unknown command"),
            (new[] { "trust", "issue", "--developer", "Dev" }, "Unknown command"),
            (new[] { "parameter", "list" }, "Unknown command"),
            (new[] { "status" }, "Unknown command")
        })
        {
            // Arrange
            using var fixture = new CliFixture();
            fixture.Gateway();

            // Act and assert
            (await fixture.Application().ExecuteAsync(args, CancellationToken.None)).ShouldBe(2);
            fixture.Error.ToString().ShouldContain(expected, Case.Sensitive);
            fixture.Runner.Calls.ShouldBeEmpty();
        }
    }

    /// <summary>Deferred single-resource areas are never blindly sent to dotnet.</summary>
    /// <returns>A task representing command mapping verification.</returns>
    [Fact(DisplayName = "Cohesion Test [Tooling.Cli] - New: Should identify every deferred area")]
    public async Task New_WithDeferredArea_ShouldNameGapAsync()
    {
        // Arrange
        using var fixture = new CliFixture();

        // Act
        foreach (string area in new[] { "apimanager", "emailhub", "eventhub", "iothub", "loadbalancer", "logspace", "mediahub",
            "messagehub", "natgateway", "notificationhub", "scheduler", "vpngateway" })
        {
            (await fixture.Application().ExecuteAsync(["new", "cohesion-" + area], CancellationToken.None)).ShouldBe(2);
        }

        // Assert
        fixture.Error.ToString().Split("No template ships for this area yet").Length.ShouldBe(13);
        fixture.Runner.Calls.ShouldBeEmpty();
    }

    /// <summary>Version removes SDK commit metadata without changing prerelease identifiers.</summary>
    /// <returns>A task representing command mapping verification.</returns>
    [Fact(DisplayName = "Cohesion Test [Tooling.Cli] - Version: Should match canonical version without commit metadata")]
    public async Task Version_WithBuildMetadata_ShouldStripSuffixAsync()
    {
        // Arrange
        using var fixture = new CliFixture();
        string informational = typeof(CliApplication).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;

        // Act
        (await fixture.Application().ExecuteAsync(["--version"], CancellationToken.None)).ShouldBe(0);

        // Assert
        CliApplication.StripBuildMetadata("10.0.0-preview.1+abc123").ShouldBe("10.0.0-preview.1");
        fixture.Output.ToString().Trim().ShouldBe(informational.Split('+')[0]);
        fixture.Output.ToString().ShouldNotContain("+");
        CliApplication.Version.ShouldBe("10.0.0-preview.1");
    }

    /// <summary>Help and malformed commands never start a process.</summary>
    /// <returns>A task representing command mapping verification.</returns>
    [Fact(DisplayName = "Cohesion Test [Tooling.Cli] - Help: Should describe verbs and reject missing values")]
    public async Task Execute_WithHelpOrMissingArguments_ShouldAvoidProcessesAsync()
    {
        // Arrange
        using var fixture = new CliFixture();

        // Act and assert
        (await fixture.Application().ExecuteAsync(["--help"], CancellationToken.None)).ShouldBe(0);
        foreach (string verb in new[] { "new", "run", "publish", "login" })
        {
            fixture.Output.ToString().ShouldContain(verb, Case.Sensitive);
        }
        foreach (string retired in new[] { "deploy", "trust", "parameter", "status" })
        {
            fixture.Output.ToString().ShouldNotContain($"  {retired} ", Case.Sensitive);
        }
        foreach (string[] args in new[] { new[] { "run", "--project" }, new[] { "new" }, new[] { "unsupported" } })
        {
            (await fixture.Application().ExecuteAsync(args, CancellationToken.None)).ShouldBe(2);
        }
        fixture.Runner.Calls.ShouldBeEmpty();
    }
}
