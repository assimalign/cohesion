using System;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Hosting.Resources;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Hosting;

namespace Assimalign.Cohesion.Web.Hosting.Resources.Tests;

public sealed class BootstrapVerifierLifetimeTests
{
    private readonly ITestOutputHelper _output;

    public BootstrapVerifierLifetimeTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting.Resources] - Bootstrap: pipeline lifetime reuses signature handles for valid tokens")]
    public async Task InvokeAsync_ValidTokensWithinPipelineLifetime_ShouldAvoidSignatureHandleAllocations()
    {
        // Arrange: both paths reuse parsed keys and profiles; only the pipeline owns a retained handle.
        using var identity = new TestBootstrapIdentity("tests", "gateway");
        ResourceContext owned = CreateContext(identity);
        ResourceContext unowned = CreateContext(identity);
        var plane = new RecordingControlPlane();
        await using WebApplication application = WebApplication.CreateBuilder().Build();
        var builder = (IWebApplicationPipelineBuilder)application;
        builder.UseResourceControlPlane(plane, owned, () => true);
        _ = builder.Build();
        await using var exchange = new ControlPlaneExchange("/cohesion/v1/endpoints", HttpMethod.Head);
        exchange.Request.Headers[HttpHeaderKey.Authorization] = "Bearer " + identity.Issue("resource");
        for (int index = 0; index < 64; index++)
        {
            Invoke(owned);
            Invoke(unowned);
        }

        // Act: signed, accepted tokens force the cryptographic verification path on every call.
        const int count = 128;
        long temporaryBytes = Measure(unowned);
        long retainedBytes = Measure(owned);
        await ((IAsyncDisposable)application).DisposeAsync();
        long releasedBytes = Measure(owned);

        // Assert: teardown releases the handle even while the resource context remains reachable.
        _output.WriteLine($"Temporary: {temporaryBytes / count} B/request; retained: {retainedBytes / count} B/request; released: {releasedBytes / count} B/request.");
        (temporaryBytes - retainedBytes).ShouldBeGreaterThan(256L * count);
        (releasedBytes - retainedBytes).ShouldBeGreaterThan(256L * count);

        long Measure(ResourceContext resource)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int index = 0; index < count; index++)
            {
                Invoke(resource);
            }
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }

        void Invoke(ResourceContext resource)
        {
            ResourceControlPlaneMiddleware.InvokeAsync(plane, resource, true, null, exchange,
                static _ => Task.CompletedTask).GetAwaiter().GetResult();
            exchange.Response.StatusCode.ShouldBe(HttpStatusCode.Ok);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting.Resources] - Bootstrap: disposing one pipeline preserves another owner and closes its own delegate")]
    public async Task InvokeAsync_SharedContextOwnersDispose_ShouldKeepOtherPipelineAliveAndRejectDisposedPipeline()
    {
        // Arrange
        using var identity = new TestBootstrapIdentity("tests", "gateway");
        ResourceContext resource = CreateContext(identity);
        await using WebApplication first = WebApplication.CreateBuilder().Build();
        await using WebApplication second = WebApplication.CreateBuilder().Build();
        IWebApplicationPipeline firstPipeline = Compose(first);
        IWebApplicationPipeline secondPipeline = Compose(second);
        await using var exchange = new ControlPlaneExchange("/cohesion/v1/endpoints", HttpMethod.Head);
        exchange.Request.Headers[HttpHeaderKey.Authorization] = "Bearer " + identity.Issue("resource");
        await firstPipeline.ExecuteAsync(exchange);

        // Act
        await ((IAsyncDisposable)first).DisposeAsync();
        await secondPipeline.ExecuteAsync(exchange);

        // Assert
        exchange.Response.StatusCode.ShouldBe(HttpStatusCode.Ok);
        await Should.ThrowAsync<ObjectDisposedException>(() => firstPipeline.ExecuteAsync(exchange));
        await ((IAsyncDisposable)second).DisposeAsync();
        await Should.ThrowAsync<ObjectDisposedException>(() => secondPipeline.ExecuteAsync(exchange));

        IWebApplicationPipeline Compose(WebApplication application)
        {
            var builder = (IWebApplicationPipelineBuilder)application;
            builder.UseResourceControlPlane(new RecordingControlPlane(), resource, () => true);
            return builder.Build();
        }
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting.Resources] - Bootstrap: final teardown releases middleware after a service stop fails")]
    public async Task DisposeAsync_ServiceStopFailure_ShouldReleaseMiddlewareLifetimeAndPreserveFailure()
    {
        // Arrange
        using var identity = new TestBootstrapIdentity("tests", "gateway");
        ResourceContext resource = CreateContext(identity);
        var stopFailure = new InvalidOperationException("The service could not stop cleanly.");
        var service = new ThrowingStopHostService(stopFailure);
        WebApplicationBuilder applicationBuilder = WebApplication.CreateBuilder();
        applicationBuilder.AddService(_ => service);
        WebApplication application = applicationBuilder.Build();
        try
        {
            var pipelineBuilder = (IWebApplicationPipelineBuilder)application;
            pipelineBuilder.UseResourceControlPlane(new RecordingControlPlane(), resource, () => true);
            IWebApplicationPipeline pipeline = pipelineBuilder.Build();
            await using var exchange = new ControlPlaneExchange("/cohesion/v1/endpoints", HttpMethod.Head);
            exchange.Request.Headers[HttpHeaderKey.Authorization] = "Bearer " + identity.Issue("resource");
            await ((IHost)application).StartAsync(CancellationToken.None);
            await pipeline.ExecuteAsync(exchange, CancellationToken.None);
            exchange.Response.StatusCode.ShouldBe(HttpStatusCode.Ok);

            // Act
            InvalidOperationException failure = await Should.ThrowAsync<InvalidOperationException>(() =>
                ((IAsyncDisposable)application).DisposeAsync().AsTask());

            // Assert
            failure.ShouldBeSameAs(stopFailure);
            await Should.ThrowAsync<ObjectDisposedException>(() => pipeline.ExecuteAsync(exchange, CancellationToken.None));
            service.DisposeCount.ShouldBe(1);
            await Should.ThrowAsync<ObjectDisposedException>(() => ((IHost)application).StartAsync(CancellationToken.None));
        }
        finally
        {
            // Also clean up if a regressed failed-disposal path allowed the host to restart.
            try
            {
                await ((IAsyncDisposable)application).DisposeAsync();
            }
            catch (InvalidOperationException exception) when (ReferenceEquals(exception, stopFailure))
            {
            }
        }
    }

    private static ResourceContext CreateContext(TestBootstrapIdentity identity) =>
        new("tests", "resource", "Testing", "gateway", contentRootPath: null, endpoints: null, mounts: null,
            settings: null, references: null, bootstrapCredential: ReadOnlyMemory<byte>.Empty,
            applicationTrustKey: identity.PublicKey, ambientValues: null);
}
