using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Core;
using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Hosting.Resources;
using Assimalign.Cohesion.Web.Testing.TestHost;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Web.Testing.Tests;

public sealed class WebApplicationProgramTestFactoryTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    [Fact(DisplayName = "Cohesion Test [Web.Testing] - FromProgram: drives the real resource Program end to end")]
    public async Task FromProgram_WithAmbientContext_ShouldDriveProgramEndToEnd()
    {
        using var cancellation = new CancellationTokenSource(TestTimeout);
        ResourceContext prior = ResourceRuntime.Current;
        ResourceContext context = CreateContext("first-host", "alpha", out Uri endpoint);
        await using IWebApplicationProgramTestFactory factory =
            WebApplicationTestFactory.FromProgram<Program>(new WebApplicationProgramTestFactoryOptions
            {
                ResourceContext = context,
                Arguments = ["forwarded"],
            });

        using HttpClient client = factory.CreateClient();
        string payload = await client.GetStringAsync("/ambient", cancellation.Token);
        string authorization = await client.GetStringAsync(
            "/request-authorization",
            cancellation.Token);
        using HttpResponseMessage readiness = await client.GetAsync("/readyz", cancellation.Token);
        using HttpResponseMessage namespacedReadiness = await client.GetAsync(
            "/cohesion/v1/readyz",
            cancellation.Token);
        using var authenticatedReadinessRequest = new HttpRequestMessage(
            HttpMethod.Get,
            "/cohesion/v1/readyz");
        authenticatedReadinessRequest.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            Encoding.UTF8.GetString(context.BootstrapCredential.Span));
        using HttpResponseMessage authenticatedReadiness = await client.SendAsync(
            authenticatedReadinessRequest,
            cancellation.Token);

        payload.ShouldBe(
            "first-host|Testing|alpha|forwarded|mounted|" +
            "cohesion-db://127.0.0.1:15740|" + Convert.ToBase64String(context.BootstrapCredential.Span));
        authorization.ShouldBe("none");
        readiness.StatusCode.ShouldBe(HttpStatusCode.OK);
        namespacedReadiness.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        authenticatedReadiness.StatusCode.ShouldBe(HttpStatusCode.OK);
        factory.Application.ShouldNotBeNull();
        ((IHost)factory.Application).Context.Environment.Name.ShouldBe("Testing");
        factory.ResourceContext.Endpoints["http"].ShouldBe(endpoint);
        ResourceRuntime.Current.ShouldBeSameAs(prior);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Testing] - FromProgram: keeps two in-process resource scopes isolated")]
    public async Task FromProgram_WithConcurrentScopes_ShouldKeepHostsIsolated()
    {
        using var cancellation = new CancellationTokenSource(TestTimeout);
        ResourceContext firstContext = CreateContext("first-host", "alpha", out Uri firstEndpoint);
        ResourceContext secondContext = CreateContext("second-host", "beta", out Uri secondEndpoint);
        await using IWebApplicationProgramTestFactory first =
            WebApplicationTestFactory.FromProgram<Program>(new WebApplicationProgramTestFactoryOptions
            {
                ResourceContext = firstContext,
                Arguments = ["one"],
            });
        await using IWebApplicationProgramTestFactory second =
            WebApplicationTestFactory.FromProgram<Program>(new WebApplicationProgramTestFactoryOptions
            {
                ResourceContext = secondContext,
                Arguments = ["two"],
            });

        await Task.WhenAll(
            first.StartAsync(cancellation.Token),
            second.StartAsync(cancellation.Token));
        using HttpClient firstClient = first.CreateClient();
        using HttpClient secondClient = second.CreateClient();

        string[] payloads = await Task.WhenAll(
            firstClient.GetStringAsync("/ambient", cancellation.Token),
            secondClient.GetStringAsync("/ambient", cancellation.Token));

        payloads[0].ShouldBe(
            "first-host|Testing|alpha|one|mounted|" +
            "cohesion-db://127.0.0.1:15740|" + Convert.ToBase64String(firstContext.BootstrapCredential.Span));
        payloads[1].ShouldBe(
            "second-host|Testing|beta|two|mounted|" +
            "cohesion-db://127.0.0.1:15740|" + Convert.ToBase64String(secondContext.BootstrapCredential.Span));
        first.ResourceContext.Endpoints["http"].ShouldBe(firstEndpoint);
        second.ResourceContext.Endpoints["http"].ShouldBe(secondEndpoint);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Testing] - FromProgram: rejects a marker without an executable entry point")]
    public void FromProgram_WithNonEntryPointMarker_ShouldRejectMarker()
    {
        InvalidOperationException exception = Should.Throw<InvalidOperationException>(
            () => WebApplicationTestFactory.FromProgram<WebApplicationProgramTestFactoryTests>());

        exception.Message.ShouldContain("not the entry-point Program", Case.Insensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Testing] - FromProgram: managed contexts require a bootstrap credential")]
    public void FromProgram_WithManagedContextWithoutCredential_ShouldRejectContext()
    {
        Uri endpoint = Uri.CreateEndpoint("http", "127.0.0.1", ReservePort());
        var context = new ResourceContext(
            gatewayName: "inprocess",
            contentRootPath: null,
            endpoints: new Dictionary<string, Uri> { ["http"] = endpoint });

        ArgumentException exception = Should.Throw<ArgumentException>(
            () => WebApplicationTestFactory.FromProgram<Program>(
                new WebApplicationProgramTestFactoryOptions { ResourceContext = context }));

        exception.Message.ShouldContain("bootstrap credential", Case.Insensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Testing] - FromProgram: requires an explicit resource scope")]
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026",
        Justification = "The test host's generated Program entry point is compiler-rooted.")]
    public void ResourceRuntime_AfterReadingCurrent_ShouldStillRejectUnscopedProgramInvocation()
    {
        _ = ResourceRuntime.Current;

        InvalidOperationException exception = Should.Throw<InvalidOperationException>(
            () => ResourceRuntime.InvokeEntry(typeof(Program).Assembly, []));

        exception.Message.ShouldContain("CreateScope", Case.Insensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Testing] - FromProgram: cancellation stops a starting resource Program")]
    public async Task FromProgram_WhenStartIsCancelled_ShouldStopProgram()
    {
        using var timeout = new CancellationTokenSource(TestTimeout);
        ResourceContext context = CreateContext("cancelled-host", "unhealthy", out _);
        await using IWebApplicationProgramTestFactory factory =
            WebApplicationTestFactory.FromProgram<Program>(new WebApplicationProgramTestFactoryOptions
            {
                ResourceContext = context,
            });
        using var cancellation = new CancellationTokenSource();
        Task start = factory.StartAsync(cancellation.Token);
        IHost host = await WaitForApplicationAsync(factory, start, timeout.Token);

        cancellation.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(
            () => start);

        factory.IsStarted.ShouldBeFalse();
        host.Context.State.ShouldBe(HostState.Stopped);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Testing] - FromProgram: cancellation remains primary when Program faults during cleanup")]
    public async Task FromProgram_WhenProgramFaultsDuringCancelledStart_ShouldPreserveCancellation()
    {
        using var timeout = new CancellationTokenSource(TestTimeout);
        ResourceContext context = CreateContext("faulted-host", "unhealthy-then-throw", out _);
        IWebApplicationProgramTestFactory factory =
            WebApplicationTestFactory.FromProgram<Program>(new WebApplicationProgramTestFactoryOptions
            {
                ResourceContext = context,
            });
        try
        {
            using var cancellation = new CancellationTokenSource();
            Task start = factory.StartAsync(cancellation.Token);
            _ = await WaitForApplicationAsync(factory, start, timeout.Token);

            cancellation.Cancel();

            await Should.ThrowAsync<OperationCanceledException>(() => start);
        }
        finally
        {
            await Should.ThrowAsync<InvalidOperationException>(
                () => factory.DisposeAsync().AsTask());
        }
    }

    private static async Task<IHost> WaitForApplicationAsync(
        IWebApplicationProgramTestFactory factory,
        Task start,
        CancellationToken cancellationToken)
    {
        while (!start.IsCompleted)
        {
            try
            {
                return (IHost)factory.Application;
            }
            catch (InvalidOperationException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken);
            }
        }

        await start;
        throw new InvalidOperationException("The unready resource unexpectedly completed startup.");
    }

    private static ResourceContext CreateContext(
        string resourceName,
        string marker,
        out Uri endpoint)
    {
        using var identity = new TestBootstrapIdentity("tests", "inprocess");
        endpoint = Uri.CreateEndpoint("http", "127.0.0.1", ReservePort());
        return new ResourceContext(
            applicationName: "tests",
            resourceName: resourceName,
            contentRootPath: null,
            environmentName: "Testing",
            gatewayName: "inprocess",
            endpoints: new Dictionary<string, Uri> { ["http"] = endpoint },
            mounts: new Dictionary<string, ResourceMount>
            {
                ["fixture"] = ResourceMount.FromBytes("mounted"u8),
            },
            settings: new Dictionary<string, string> { ["Test:Marker"] = marker },
            references: new Dictionary<string, Uri>
            {
                ["inventory-database:db"] = Uri.CreateEndpoint(
                    "cohesion-db",
                    "127.0.0.1",
                    15740),
            },
            bootstrapCredential: Encoding.UTF8.GetBytes(identity.Issue(resourceName)),
            applicationTrustKey: identity.PublicKey,
            ambientValues: null);
    }

    private static int ReservePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }
}
