using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Tests;

/// <summary>
/// Drives the deployment-record management API end to end: a real Web.Hosting fixture, described the way the SDK
/// describes it, realized by a Local gateway, and changed through the gateway's management provider.
/// </summary>
[Collection(LocalGatewayConsoleCollection.Name)]
public sealed class LocalGatewayManagementTests
{
    private const string ApplicationName = "managed-tests";
    private const string HostAssembly = "Assimalign.Cohesion.Web.ManagedHost";

    private static readonly ApplicationCaller _operator = new(
        null, "operator@tests", ApplicationCallerKind.Operator, [], [ResourceManagementOperations.All]);

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Management: a Local resource applies its deployment layer and live overrides, and only a new generation discards them")]
    public async Task LocalGateway_ManagedResource_ShouldKeepOverridesUntilTheNextDeployment()
    {
        string root = CreateDirectory();
        try
        {
            ResourceSurface surface = await DescribeAsync(root);
            surface.Configuration.Select(static section => section.Path.Value)
                .ShouldBe(["Fixture:Greeting", "Fixture:Pool", "Fixture:Schema"]);

            string recorded;
            await using (Session first = await Session.StartAsync(root, surface, """{"message":"from-code"}"""))
            {
                // The Startup phase applied the deployment layer before the host started.
                (await first.GetGreetingAsync()).ShouldBe("from-code");

                ResourceManagementResult configuration = await first.Gateway.GetConfigurationAsync(ApplicationName, "web");
                configuration.Status.ShouldBe(ResourceManagementStatus.Ok);
                JsonElement greeting = Section(configuration, "Fixture:Greeting");
                greeting.GetProperty("layers").GetProperty("deployment").GetProperty("message").GetString().ShouldBe("from-code");
                greeting.GetProperty("overridable").GetBoolean().ShouldBeTrue();

                // A live override reaches the running instance without a restart.
                ResourceManagementResult patch = await first.Gateway.ChangeOverrideAsync(
                    ApplicationName, "web", "Fixture:Greeting", ResourceOverrideChangeKind.Merge,
                    Json("""{"message":"from-operator"}"""), configuration.Version, _operator);
                patch.Status.ShouldBe(ResourceManagementStatus.Ok, Text(patch));
                await EventuallyAsync(first.GetGreetingAsync, "from-operator");
                await EventuallyAsync(async () => Root(await first.Gateway.GetConfigurationStatusAsync(ApplicationName, "web"))
                    .GetProperty("converged").GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture), "1");

                // Actions run on the instance and store nothing.
                ResourceManagementResult echo = await first.Gateway.InvokeActionAsync(
                    ApplicationName, "web", "echo", Json("""{"text":"hi"}"""), _operator);
                echo.Status.ShouldBe(ResourceManagementStatus.Ok, Text(echo));
                Root(echo).GetProperty("results")[0].GetProperty("result").GetProperty("text").GetString().ShouldBe("hi");

                // Claims add keyed entries to a claimable section; one entry has one owner.
                (await first.Gateway.ReplaceClaimsAsync(ApplicationName, "web", "appb",
                    Json("""{"Fixture:Schema":{"invoices":{"columns":4}}}"""), null, _operator)).Status.ShouldBe(ResourceManagementStatus.Ok);
                (await first.Gateway.ReplaceClaimsAsync(ApplicationName, "web", "appc",
                    Json("""{"Fixture:Schema":{"invoices":{"columns":5}}}"""), null, _operator)).Status.ShouldBe(ResourceManagementStatus.ClaimConflict);

                // Refusals, decided against the static surface.
                (await Override(first, "Fixture:Schema", """{"invoices":{"columns":1}}""")).Status.ShouldBe(ResourceManagementStatus.NotOverridable);
                (await Override(first, "Fixture:Missing", "{}")).Status.ShouldBe(ResourceManagementStatus.UnknownSection);
                (await Override(first, "Fixture:Greeting", """{"mesage":"typo"}""")).Status.ShouldBe(ResourceManagementStatus.InvalidConfiguration);
                (await first.Gateway.ChangeOverrideAsync(ApplicationName, "web", "Fixture:Greeting", ResourceOverrideChangeKind.Merge,
                    Json("""{"repeat":2}"""), configuration.Version, _operator)).Status.ShouldBe(ResourceManagementStatus.VersionMoved);
                (await first.Gateway.ReplaceClaimsAsync(ApplicationName, "web", "appb",
                    Json("""{"Fixture:Greeting":{"x":{}}}"""), null, _operator)).Status.ShouldBe(ResourceManagementStatus.NotClaimable);
                (await first.Gateway.InvokeActionAsync(ApplicationName, "web", "missing", default, _operator)).Status.ShouldBe(ResourceManagementStatus.UnknownAction);
                (await first.Gateway.InvokeActionAsync(ApplicationName, "web", "echo", Json("""{"text":5}"""), _operator)).Status.ShouldBe(ResourceManagementStatus.InvalidInput);
                recorded = (await first.Gateway.GetConfigurationAsync(ApplicationName, "web")).Version!;
            }

            // An identical re-apply is the same generation: the record, override included, survives the gateway restart.
            await using (Session second = await Session.StartAsync(root, surface, """{"message":"from-code"}"""))
            {
                (await second.GetGreetingAsync()).ShouldBe("from-operator");
                ResourceManagementResult configuration = await second.Gateway.GetConfigurationAsync(ApplicationName, "web");
                configuration.Version.ShouldBe(recorded);
            }

            // Changed deployment content is a new generation: the override is discarded and the code value returns;
            // claims belong to their owner and survive.
            await using (Session third = await Session.StartAsync(root, surface, """{"message":"from-code-2"}"""))
            {
                (await third.GetGreetingAsync()).ShouldBe("from-code-2");
                ResourceManagementResult configuration = await third.Gateway.GetConfigurationAsync(ApplicationName, "web");
                JsonElement layers = Section(configuration, "Fixture:Greeting").GetProperty("layers");
                layers.TryGetProperty("override", out _).ShouldBeFalse();
                Section(configuration, "Fixture:Schema").GetProperty("layers").GetProperty("claims").GetProperty("appb")
                    .GetProperty("invoices").GetProperty("columns").GetInt32().ShouldBe(4);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static Task<ResourceManagementResult> Override(Session session, string section, string patch) =>
        session.Gateway.ChangeOverrideAsync(ApplicationName, "web", section, ResourceOverrideChangeKind.Merge, Json(patch), null, _operator).AsTask();

    // Runs the fixture with COHESION_DESCRIBE_PATH, the way the SDK's describe step does after each build.
    private static async Task<ResourceSurface> DescribeAsync(string root)
    {
        string path = Path.Combine(root, "resource.runtime.json");
        var start = new ProcessStartInfo(AppHostPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        foreach (string key in start.Environment.Keys.Where(static key => key.StartsWith("COHESION_", StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            start.Environment.Remove(key);
        }

        start.Environment["COHESION_DESCRIBE_PATH"] = path;
        using Process process = Process.Start(start)!;
        Task<string> errors = process.StandardError.ReadToEndAsync();
        _ = process.StandardOutput.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await process.WaitForExitAsync(timeout.Token);
        process.ExitCode.ShouldBe(0, await errors);
        return ResourceSurface.Load(path);
    }

    private static async Task EventuallyAsync(Func<Task<string>> read, string expected)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        string last = string.Empty;
        while (!timeout.IsCancellationRequested)
        {
            last = await read();
            if (last == expected)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), CancellationToken.None);
        }

        last.ShouldBe(expected);
    }

    private static JsonElement Section(ResourceManagementResult result, string path) =>
        Root(result).GetProperty("sections").EnumerateArray().Single(section => section.GetProperty("path").GetString() == path);

    private static JsonElement Root(ResourceManagementResult result)
    {
        using JsonDocument document = JsonDocument.Parse(result.Document);
        return document.RootElement.Clone();
    }

    private static string Text(ResourceManagementResult result) => Encoding.UTF8.GetString(result.Document.Span);

    private static ReadOnlyMemory<byte> Json(string value) => Encoding.UTF8.GetBytes(value);

    private static string AppHostPath =>
        Path.Combine(AppContext.BaseDirectory, HostAssembly + (OperatingSystem.IsWindows() ? ".exe" : string.Empty));

    private static string CreateDirectory() =>
        Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "managed-" + Guid.NewGuid().ToString("N"))).FullName;

    private sealed class Session : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cancellation;
        private readonly Task _run;
        private readonly HttpClient _client = new();
        private readonly Uri _greeting;

        private Session(LocalGateway gateway, CancellationTokenSource cancellation, Task run, Uri greeting)
        {
            Gateway = gateway;
            _cancellation = cancellation;
            _run = run;
            _greeting = greeting;
        }

        public LocalGateway Gateway { get; }

        public static async Task<Session> StartAsync(string root, ResourceSurface surface, string greeting)
        {
            var gateway = new LocalGateway(new LocalGatewayOptions
            {
                StateDirectory = root,
                BaseDirectory = AppContext.BaseDirectory,
                ReadinessBudget = TimeSpan.FromSeconds(25),
                ProbeInterval = TimeSpan.FromMilliseconds(100),
                ProbeTimeout = TimeSpan.FromSeconds(3),
                StopGrace = TimeSpan.FromSeconds(3),
            });
            var details = new ConcurrentQueue<string>();
            gateway.ResourceStates.StateChanged += (_, change) => details.Enqueue(change.Detail ?? change.Current.ToString());
            IApplicationBuilder builder = Application.CreateBuilder(ApplicationName, ["--environment", AppEnvironment.Keys.Local])
                .UseGateway(gateway);
            IApplicationResourceDescriptor web = builder.AddResource(Manifest(surface)).Configure("Fixture:Greeting", greeting);
            IApplication application = builder.Build();
            var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            Task run = application.RunAsync(cancellation.Token);
            ResourceLifecycle reached = await gateway.ResourceStates.WaitForStateAsync(
                web.Resource.Id,
                new HashSet<ResourceLifecycle> { ResourceLifecycle.Running, ResourceLifecycle.Failed, ResourceLifecycle.Stopped },
                TimeSpan.FromSeconds(35));
            reached.ShouldBe(ResourceLifecycle.Running, string.Join("\n", details));
            ResourceEndpoint endpoint = gateway.ResourceStates.GetObservedEndpoints(web.Resource.Id).Single();
            return new Session(gateway, cancellation, run, new Uri(Uri.CreateEndpoint(endpoint.Scheme, endpoint.Host!, endpoint.Port), "/greeting"));
        }

        public async Task<string> GetGreetingAsync()
        {
            using HttpResponseMessage response = await _client.GetAsync(_greeting);
            response.EnsureSuccessStatusCode();
            using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
            return document.RootElement.GetProperty("message").GetString()!;
        }

        public async ValueTask DisposeAsync()
        {
            _cancellation.Cancel();
            try
            {
                await _run.WaitAsync(TimeSpan.FromSeconds(20));
            }
            catch (OperationCanceledException)
            {
            }

            _client.Dispose();
            _cancellation.Dispose();
        }

        private static ResourceManifest Manifest(ResourceSurface surface) => new ResourceManifest
        {
            Name = "web",
            Application = ApplicationName,
            Kind = "Web",
            ApplicationModel = "Assimalign.Cohesion.Test.ApplicationModel",
            Artifact = new ResourceManifestArtifact { Assembly = HostAssembly + ".dll", AppHost = AppHostPath },
            Endpoints = [new ResourceManifestEndpoint { Name = "http", Scheme = "http", Protocol = "tcp", ContainerPort = 8080 }],
            ControlPlane = new ResourceManifestControlPlane { Endpoint = "http", Path = "/cohesion/v1" },
            Lifecycle = new ResourceManifestLifecycle { Workload = WorkloadKind.Deployment, Replicas = 1, RestartPolicy = "Never", StopGraceSeconds = 3 },
        } with { Surface = surface };
    }
}
