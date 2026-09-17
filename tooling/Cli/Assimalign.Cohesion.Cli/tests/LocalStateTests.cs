using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Cli.Tests;

/// <summary>Tests the gateway's local wire contracts and read-only status requests.</summary>
public sealed class LocalStateTests
{
    /// <summary>The produced bytes satisfy the gateway's actual DPAPI and JsonDocument reader contract.</summary>
    [Fact(DisplayName = "Cohesion Test [Tooling.Cli] - Parameter: Should round-trip through gateway reader contract")]
    public async Task Parameter_WithValues_ShouldProduceGatewayReadableBytesAsync()
    {
        using var fixture = new CliFixture();
        fixture.Gateway();
        string value = "synthetic value with spaces \"quotes\" and Unicode λ";

        (await fixture.Application().ExecuteAsync(["parameter", "set", "sample-key", value], CancellationToken.None)).ShouldBe(0);
        (await fixture.Application("line one\nline two\n").ExecuteAsync(["parameter", "set", "multiline", "--stdin"], CancellationToken.None)).ShouldBe(0);
        (await fixture.Application().ExecuteAsync(["parameter", "set", "empty", ""], CancellationToken.None)).ShouldBe(0);

        string path = Path.Combine(fixture.ApplicationDirectory, "parameters.json");
        byte[] persisted = File.ReadAllBytes(path);
        // This is the byte/JSON contract from ApplicationGateway.ReadParametersAsync:
        // DPAPI CurrentUser with null entropy; JSON object; each property's value is a string.
        byte[] plaintext = OperatingSystem.IsWindows()
            ? ProtectedData.Unprotect(persisted, optionalEntropy: null, DataProtectionScope.CurrentUser)
            : persisted;
        try
        {
            using JsonDocument document = JsonDocument.Parse(plaintext);
            document.RootElement.ValueKind.ShouldBe(JsonValueKind.Object);
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                property.Value.ValueKind.ShouldBe(JsonValueKind.String);
            }
            document.RootElement.GetProperty("sample-key").GetString().ShouldBe(value);
            document.RootElement.GetProperty("multiline").GetString().ShouldBe("line one\nline two\n");
            document.RootElement.GetProperty("empty").GetString().ShouldBe("");
            if (OperatingSystem.IsWindows())
            {
                Encoding.UTF8.GetString(persisted).ShouldNotContain(value, Case.Sensitive);
            }
            else
            {
                File.GetUnixFileMode(path).ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(persisted);
            CryptographicOperations.ZeroMemory(plaintext);
        }
        (await fixture.Application().ExecuteAsync(["parameter", "list"], CancellationToken.None)).ShouldBe(0);
        fixture.Output.ToString().ShouldBe($"empty{Environment.NewLine}multiline{Environment.NewLine}sample-key{Environment.NewLine}");
        fixture.Output.ToString().ShouldNotContain(value, Case.Sensitive);
        (await fixture.Application().ExecuteAsync(["parameter", "remove", "sample-key"], CancellationToken.None)).ShouldBe(0);
        byte[] after = ProtectedFile.Read(path);
        using JsonDocument remaining = JsonDocument.Parse(after);
        remaining.RootElement.TryGetProperty("sample-key", out _).ShouldBeFalse();
        remaining.RootElement.GetProperty("multiline").GetString().ShouldBe("line one\nline two\n");
        Directory.GetFiles(fixture.ApplicationDirectory, "*.tmp").ShouldBeEmpty();
        fixture.Runner.Calls.ShouldBeEmpty();
    }

    /// <summary>Invalid value types cannot be rewritten as a corrupt gateway parameter file.</summary>
    [Fact(DisplayName = "Cohesion Test [Tooling.Cli] - Parameter: Should reject non-string JSON without rewriting")]
    public async Task Parameter_WithInvalidDocument_ShouldPreserveFileAsync()
    {
        foreach (string document in new[] { "{\"key\":42}", "{\"key\":null}", "{\"key\":\"one\",\"key\":\"two\"}", "[]" })
        {
            using var fixture = new CliFixture();
            fixture.Gateway();
            string path = Path.Combine(fixture.ApplicationDirectory, "parameters.json");
            ProtectedFile.Write(path, Encoding.UTF8.GetBytes(document));
            byte[] before = File.ReadAllBytes(path);

            (await fixture.Application().ExecuteAsync(["parameter", "set", "new-key", "synthetic"], CancellationToken.None)).ShouldNotBe(0);

            File.ReadAllBytes(path).ShouldBe(before);
            fixture.Output.ToString().ShouldBeEmpty();
        }
    }

    /// <summary>Value and stdin selection are mutually exclusive and values can start with a hyphen.</summary>
    [Fact(DisplayName = "Cohesion Test [Tooling.Cli] - Parameter: Should validate input mode and honor state overrides")]
    public async Task Parameter_WithInputOptions_ShouldHonorStateRootAsync()
    {
        using var fixture = new CliFixture();
        fixture.Gateway();
        (await fixture.Application().ExecuteAsync(["parameter", "set", "key", "value", "--stdin"], CancellationToken.None)).ShouldBe(2);
        (await fixture.Application().ExecuteAsync(["parameter", "set", "key"], CancellationToken.None)).ShouldBe(2);
        (await fixture.Application().ExecuteAsync(["parameter", "set", "key", "-leading", "--app", "override", "--state-root", "custom"], CancellationToken.None)).ShouldBe(0);
        File.Exists(Path.Combine(fixture.Root, "custom", "override", "parameters.json")).ShouldBeTrue();
        Directory.Exists(fixture.StateRoot).ShouldBeFalse();
    }

    /// <summary>Status ignores the model graph and private trust subtree.</summary>
    [Fact(DisplayName = "Cohesion Test [Tooling.Cli] - Status: Should read declarations ports owner and process identity")]
    public async Task Status_WithFiles_ShouldDescribeLocalEvidenceAsync()
    {
        using var fixture = new CliFixture();
        fixture.Gateway();
        WriteExport(fixture);
        fixture.Write("Gateway/.cohesion/sample/.state/ports.json", "{\"controlPlane\":5010,\"resources\":{\"api\":{\"http\":5011}}}");
        fixture.Write("Gateway/.cohesion/sample/.state/owner", "sample@local");
        using Process current = Process.GetCurrentProcess();
        fixture.Write("Gateway/.cohesion/sample/.state/api/pid",
            $"{{\"processId\":{current.Id},\"startTimeUtcTicks\":{current.StartTime.ToUniversalTime().Ticks},\"executablePath\":\"ignored\"}}");
        fixture.Write("Gateway/.cohesion/sample/trust/local/private-key.p8.protected", "synthetic-private-sentinel");
        fixture.Write("Gateway/.cohesion/sample/trust/local/keyring/key", "synthetic-ring-sentinel");

        (await fixture.Application().ExecuteAsync(["status"], CancellationToken.None)).ShouldBe(0);

        string output = fixture.Output.ToString();
        foreach (string expected in new[] { "not observed resource state", "sample@local", "5010", "allocated http: 5011",
            "kind=Web", "manifestHash=hash", "declared http: internal=http://declared", "public=https://public", "local process=running" })
        {
            output.ShouldContain(expected, Case.Sensitive);
        }
        output.ShouldNotContain("synthetic-private", Case.Sensitive);
        output.ShouldNotContain("synthetic-ring", Case.Sensitive);
        output.ShouldNotContain("ignored-model", Case.Sensitive);
    }

    /// <summary>A reused PID must not be mistaken for the registered child.</summary>
    [Fact(DisplayName = "Cohesion Test [Tooling.Cli] - Status: Should reject mismatched process start times")]
    public void GetLiveness_WithMismatchedStartTime_ShouldNotReportRunning()
    {
        using Process current = Process.GetCurrentProcess();

        LocalStateCommands.GetLiveness(new ProcessDocument { ProcessId = current.Id, StartTimeUtcTicks = 1 })
            .ShouldBe("not running (pid/start time mismatch)");
        LocalStateCommands.GetLiveness(new ProcessDocument { ProcessId = int.MaxValue, StartTimeUtcTicks = 1 }).ShouldBe("not running");
        LocalStateCommands.GetLiveness(new ProcessDocument()).ShouldBe("not running");
    }

    /// <summary>Only the resource read route is used, with the application developer token.</summary>
    [Fact(DisplayName = "Cohesion Test [Tooling.Cli] - StatusLive: Should authenticate resource GET and print observations")]
    public async Task Status_WithLiveToken_ShouldReadObservedStateAsync()
    {
        int calls = 0;
        using var fixture = new CliFixture((request, _) =>
        {
            calls++;
            request.Method.ShouldBe(HttpMethod.Get);
            request.RequestUri!.AbsoluteUri.ShouldBe("http://localhost:5010/cohesion/v1/resources/api");
            request.Headers.Authorization!.Scheme.ShouldBe("Bearer");
            request.Headers.Authorization.Parameter.ShouldBe("synthetic-developer-token");
            return Task.FromResult(CliFixture.Json("{\"name\":\"api\",\"kind\":\"Web\",\"state\":\"Running\",\"endpoints\":[{\"name\":\"http\",\"address\":\"http://observed\",\"isPublic\":false}]}"));
        });
        fixture.Gateway();
        WriteExport(fixture);
        fixture.Write("Gateway/.cohesion/sample/control-plane.json", "{\"url\":\"http://localhost:5010\",\"trustKey\":{\"kty\":\"EC\"}}");

        (await fixture.Application(token: "synthetic-developer-token").ExecuteAsync(["status", "--live"], CancellationToken.None)).ShouldBe(0);
        (await fixture.Application(token: "unused").ExecuteAsync(["status", "--live", "--token", "synthetic-developer-token"], CancellationToken.None)).ShouldBe(0);

        calls.ShouldBe(2);
        fixture.Output.ToString().ShouldContain("observed state: Running", Case.Sensitive);
        fixture.Output.ToString().ShouldContain("http://observed", Case.Sensitive);
        fixture.Output.ToString().ShouldNotContain("synthetic-developer-token", Case.Sensitive);
    }

    /// <summary>Missing and invalid live credentials give actionable, non-secret diagnostics.</summary>
    [Fact(DisplayName = "Cohesion Test [Tooling.Cli] - StatusLive: Should explain HTTP 401 and 403")]
    public async Task Status_WithRejectedToken_ShouldExplainTrustTokenAsync()
    {
        foreach (HttpStatusCode status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden })
        {
            using var fixture = new CliFixture((_, _) => Task.FromResult(CliFixture.Json("{}", status)));
            fixture.Gateway();
            WriteExport(fixture);
            fixture.Write("Gateway/.cohesion/sample/control-plane.json", "{\"url\":\"http://localhost:5010\"}");

            (await fixture.Application().ExecuteAsync(["status", "--live"], CancellationToken.None)).ShouldBe(2);
            (await fixture.Application().ExecuteAsync(["status", "--live", "--token", "synthetic-invalid"], CancellationToken.None)).ShouldBe(2);

            fixture.Error.ToString().ShouldContain($"HTTP {(int)status}", Case.Sensitive);
            fixture.Error.ToString().ShouldContain("trust issue --developer", Case.Sensitive);
            fixture.Error.ToString().ShouldNotContain("synthetic-invalid", Case.Sensitive);
        }
    }

    /// <summary>Missing state has a direct run-first instruction.</summary>
    [Fact(DisplayName = "Cohesion Test [Tooling.Cli] - Status: Should explain missing local gateway state")]
    public async Task Status_WithoutState_ShouldSuggestRunAsync()
    {
        using var fixture = new CliFixture();
        fixture.Gateway();

        (await fixture.Application().ExecuteAsync(["status"], CancellationToken.None)).ShouldBe(2);

        fixture.Error.ToString().ShouldContain("no local gateway state; run `cohesion run` first", Case.Sensitive);
    }

    /// <summary>Malformed optional state cannot crash the executable.</summary>
    [Fact(DisplayName = "Cohesion Test [Tooling.Cli] - Status: Should reject null resource entries without crashing")]
    public async Task Status_WithMalformedResources_ShouldReportErrorAsync()
    {
        foreach (string resources in new[] { "null", "[null]" })
        {
            using var fixture = new CliFixture();
            fixture.Gateway();
            fixture.Write("Gateway/.cohesion/sample/export.json", "{\"resources\":" + resources + "}");

            (await fixture.Application().ExecuteAsync(["status"], CancellationToken.None)).ShouldBe(2);

            fixture.Error.ToString().ShouldContain("export.json", Case.Sensitive);
        }
    }

    /// <summary>Invalid header characters cannot escape through an exception message.</summary>
    [Fact(DisplayName = "Cohesion Test [Tooling.Cli] - StatusLive: Should redact malformed bearer headers")]
    public async Task Status_WithMalformedBearer_ShouldAvoidSecretDiagnosticAsync()
    {
        using var fixture = new CliFixture();
        fixture.Gateway();
        WriteExport(fixture);
        fixture.Write("Gateway/.cohesion/sample/control-plane.json", "{\"url\":\"http://localhost:5010\"}");

        (await fixture.Application().ExecuteAsync(["status", "--live", "--token", "synthetic\r\ninvalid"], CancellationToken.None)).ShouldBe(1);

        fixture.Error.ToString().ShouldNotContain("synthetic", Case.Sensitive);
        fixture.Output.ToString().ShouldNotContain("synthetic", Case.Sensitive);
    }

    private static void WriteExport(CliFixture fixture) =>
        fixture.Write("Gateway/.cohesion/sample/export.json", """
            {"application":"sample","environment":"Development","version":"1","resources":[
              {"name":"api","kind":"Web","manifestHash":"hash","endpoints":[
                {"name":"http","internal":"http://declared","public":"https://public"}]}],
              "model":{"ignored-model":"a future graph that this CLI does not validate","commands":[42]},
              "commands":[{"ignored":"outcomes"}],"trustKey":{"kty":"EC"}}
            """);
}
