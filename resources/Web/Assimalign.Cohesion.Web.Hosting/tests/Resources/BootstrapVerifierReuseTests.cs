using System;
using System.Linq;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Hosting.Resources;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Hosting;

using Assimalign.Cohesion.Web.Hosting.Resources;
using Assimalign.Cohesion.Web.Hosting.Tests.TestObjects;

namespace Assimalign.Cohesion.Web.Hosting.Tests;

public sealed class BootstrapVerifierReuseTests
{
    private readonly ITestOutputHelper _output;

    public BootstrapVerifierReuseTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Bootstrap: reused context avoids per-request JWK allocations")]
    public void InvokeAsync_RepeatedBearerRequests_ShouldAvoidVerifierConstructionAllocations()
    {
        // Arrange: invalid tokens isolate verifier construction from platform signature costs.
        using var identity = new TestBootstrapIdentity("tests", "gateway");
        ResourceContext shared = CreateContext(identity);
        ResourceContext[] cold = Enumerable.Range(0, 64).Select(_ => CreateContext(identity)).ToArray();
        var plane = new RecordingControlPlane();
        var exchange = new ControlPlaneExchange("/cohesion/v1/endpoints", HttpMethod.Get);
        exchange.Request.Headers[HttpHeaderKey.Authorization] = "Bearer invalid";
        for (int index = 0; index < 64; index++)
        {
            Invoke(shared);
        }

        // Act: the cold path retains the former per-request JWK/profile construction cost.
        long before = GC.GetAllocatedBytesForCurrentThread();
        foreach (ResourceContext context in cold)
        {
            Invoke(context);
        }
        long coldBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < cold.Length; index++)
        {
            Invoke(shared);
        }
        long warmBytes = GC.GetAllocatedBytesForCurrentThread() - before;

        // Assert: a relative comparison is stable across runtimes and native crypto providers.
        _output.WriteLine($"Cold: {coldBytes / cold.Length} B/request; reused: {warmBytes / cold.Length} B/request.");
        (coldBytes - warmBytes).ShouldBeGreaterThan(1024L * cold.Length);
        exchange.Response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        void Invoke(ResourceContext context)
        {
            Task request = ResourceControlPlaneMiddleware.InvokeAsync(plane, context, true, null, exchange,
                static _ => Task.CompletedTask);
            request.IsCompletedSuccessfully.ShouldBeTrue();
        }
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Bootstrap: rotated tokens and concurrent requests retain context isolation")]
    public async Task InvokeAsync_RotatedTokensAndConcurrentContexts_ShouldValidateEveryToken()
    {
        // Arrange: distinct keys with identical identities must never share trust.
        using var identity = new TestBootstrapIdentity("tests", "gateway");
        using var otherIdentity = new TestBootstrapIdentity("tests", "gateway");
        ResourceContext context = CreateContext(identity);
        ResourceContext other = CreateContext(otherIdentity);
        string first = identity.Issue("resource");
        string rotated = identity.Issue("resource");
        await using WebApplication application = WebApplication.CreateBuilder().Build();
        var builder = (IWebApplicationPipelineBuilder)application;
        builder.UseResourceControlPlane(new RecordingControlPlane(), context, () => true);
        builder.UseResourceControlPlane(new RecordingControlPlane(), other, () => true);
        _ = builder.Build();

        // Act
        HttpStatusCode[] statuses = await Task.WhenAll(Enumerable.Range(0, 32).Select(index => Task.Run(async () =>
        {
            await using var exchange = new ControlPlaneExchange("/cohesion/v1/endpoints", HttpMethod.Get);
            exchange.Request.Headers[HttpHeaderKey.Authorization] = "Bearer " + (index % 2 == 0 ? first : rotated);
            await ResourceControlPlaneMiddleware.InvokeAsync(new RecordingControlPlane(), index < 16 ? context : other,
                true, null, exchange, static _ => Task.CompletedTask);
            return exchange.Response.StatusCode;
        })));

        // Assert: rotation and isolation already held; this pins them while the verifier is reused.
        first.ShouldNotBe(rotated);
        statuses.Take(16).ShouldAllBe(status => status == HttpStatusCode.Ok);
        statuses.Skip(16).ShouldAllBe(status => status == HttpStatusCode.Unauthorized);
    }

    private static ResourceContext CreateContext(TestBootstrapIdentity identity) =>
        new("tests", "resource", "Testing", "gateway", contentRootPath: null, endpoints: null, mounts: null,
            settings: null, references: null, bootstrapCredential: ReadOnlyMemory<byte>.Empty,
            applicationTrustKey: identity.PublicKey, ambientValues: null);
}
