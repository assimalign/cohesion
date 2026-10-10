using System;
using System.Net;
using System.Threading.Tasks;

using Assimalign.Cohesion.DependencyInjection;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Routing;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Web.Hosting.Tests;

/// <summary>
/// Owner decision 35 (#1380): a request feature is an <see cref="IHttpFeature"/> singleton. The host
/// rejects, each with an error naming the registration, a scoped or transient feature and a feature
/// registered under a narrower contract when the application is built, and a disposable feature when the
/// pipeline snapshots the features it stamps onto every exchange.
/// </summary>
public class WebApplicationFeatureRegistrationTests
{
    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Feature registration: A scoped feature registration should fail the build and name the registration")]
    public void Build_WithScopedFeatureRegistration_ShouldThrowNamingTheRegistration()
    {
        // Arrange
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        int index = builder.Services.Container.Count;
        builder.Services.AddScoped<IHttpFeature>(_ => new TestFeature());

        // Act
        InvalidOperationException exception = Should.Throw<InvalidOperationException>(() => builder.Build());

        // Assert
        exception.Message.ShouldContain($"builder.Services[{index}]", Case.Sensitive);
        exception.Message.ShouldContain("is Scoped", Case.Sensitive);
        exception.Message.ShouldContain("must be a singleton", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Feature registration: A transient feature registration should fail the build and name the registration")]
    public void Build_WithTransientFeatureRegistration_ShouldThrowNamingTheRegistration()
    {
        // Arrange
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        ((IWebApplicationBuilder)builder).AddFeature(new TestFeature());
        int index = builder.Services.Container.Count;
        builder.Services.AddTransient<IHttpFeature>(_ => new TestFeature());

        // Act
        InvalidOperationException exception = Should.Throw<InvalidOperationException>(() => builder.Build());

        // Assert
        exception.Message.ShouldContain($"builder.Services[{index}]", Case.Sensitive);
        exception.Message.ShouldContain("is Transient", Case.Sensitive);
        exception.Message.ShouldContain("must be a singleton", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Feature registration: A rejected build should leave registration open")]
    public async Task Build_WithRejectedFeatureRegistration_ShouldLeaveTheBuilderAsComposed()
    {
        // Arrange
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Services.AddScoped<IHttpFeature>(_ => new TestFeature());
        int count = builder.Services.Container.Count;

        // Act
        Should.Throw<InvalidOperationException>(() => builder.Build());

        // Assert
        builder.Services.Container.Count.ShouldBe(count);
        builder.Services.Container.Unregister(builder.Services.Container[count - 1]).ShouldBeTrue();
        await using WebApplication application = builder.Build();
        application.ShouldNotBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Feature registration: A feature registered under a narrower contract should fail the build")]
    public void Build_WithFeatureRegisteredUnderDerivedContract_ShouldThrowNamingTheServiceType()
    {
        // Arrange
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        int index = builder.Services.Container.Count;
        builder.Services.AddSingleton<ITestFeature>(new TestFeature());

        // Act
        InvalidOperationException exception = Should.Throw<InvalidOperationException>(() => builder.Build());

        // Assert
        exception.Message.ShouldContain($"builder.Services[{index}]", Case.Sensitive);
        exception.Message.ShouldContain(typeof(ITestFeature).FullName!, Case.Sensitive);
        exception.Message.ShouldContain("is not IHttpFeature", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Feature registration: Singleton registrations from every path should build and reach the application")]
    public async Task Build_WithSingletonFeatureRegistrations_ShouldSucceed()
    {
        // Arrange
        TestFeature raw = new();
        TestFeature direct = new();
        TestFeature factory = new();
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        ((IWebApplicationBuilder)builder).AddFeature(raw);
        builder.Services.AddSingleton<IHttpFeature>(direct);
        builder.Services.AddSingleton<IHttpFeature>(_ => factory);
        builder.Services.AddRouting();

        // Act
        await using WebApplication application = builder.Build();
        IWebApplicationPipeline pipeline = ((IWebApplicationPipelineBuilder)application).Build();

        // Assert
        pipeline.ShouldNotBeNull();
        application.Context.Features.ShouldContain(raw);
        application.Context.Features.ShouldContain(direct);
        application.Context.Features.ShouldContain(factory);
        application.Context.Features.ShouldContain(feature => feature is IRouterFeature);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Feature registration: A disposable feature should fail the pipeline build and name the feature")]
    public async Task BuildPipeline_WithDisposableFeature_ShouldThrowNamingTheFeature()
    {
        // Arrange
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        ((IWebApplicationBuilder)builder).AddFeature(new DisposableFeature());
        await using WebApplication application = builder.Build();

        // Act
        InvalidOperationException exception = Should.Throw<InvalidOperationException>(
            () => ((IWebApplicationPipelineBuilder)application).Build());

        // Assert
        exception.Message.ShouldContain($"'{nameof(DisposableFeature)}'", Case.Sensitive);
        exception.Message.ShouldContain(typeof(DisposableFeature).FullName!, Case.Sensitive);
        exception.Message.ShouldContain("is disposable", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Feature registration: An async-disposable feature from a factory should fail the pipeline build")]
    public async Task BuildPipeline_WithAsyncDisposableFactoryFeature_ShouldThrowNamingTheFeature()
    {
        // Arrange
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        ((IWebApplicationBuilder)builder).AddFeature(_ => new AsyncDisposableFeature());
        await using WebApplication application = builder.Build();

        // Act
        InvalidOperationException exception = Should.Throw<InvalidOperationException>(
            () => ((IWebApplicationPipelineBuilder)application).Build());

        // Assert
        exception.Message.ShouldContain($"'{nameof(AsyncDisposableFeature)}'", Case.Sensitive);
        exception.Message.ShouldContain("is disposable", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Feature registration: A disposable feature should fail the start before any request")]
    public async Task StartAsync_WithDisposableFeature_ShouldFailBeforeServing()
    {
        // Arrange
        DisposableFeature feature = new();
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Server.UseServer(options => options.UseHttp1(
            tcp => tcp.EndPoint = new IPEndPoint(IPAddress.Loopback, 0)));
        ((IWebApplicationBuilder)builder).AddFeature(feature);
        await using WebApplication application = builder.Build();

        // Act
        Exception exception = await Should.ThrowAsync<Exception>(
            () => ((IWebApplication)application).StartAsync());

        // Assert
        Unwrap(exception).ShouldBeOfType<InvalidOperationException>()
            .Message.ShouldContain("is disposable", Case.Sensitive);
        feature.DisposeCount.ShouldBe(0);
    }

    private static Exception Unwrap(Exception exception)
    {
        while (exception is AggregateException { InnerExceptions.Count: 1 } aggregate)
        {
            exception = aggregate.InnerExceptions[0];
        }

        return exception;
    }

    private interface ITestFeature : IHttpFeature
    {
    }

    private sealed class TestFeature : ITestFeature
    {
        public string Name => nameof(TestFeature);
    }

    private sealed class DisposableFeature : IHttpFeature, IDisposable
    {
        public string Name => nameof(DisposableFeature);

        public int DisposeCount { get; private set; }

        public void Dispose() => DisposeCount++;
    }

    private sealed class AsyncDisposableFeature : IHttpFeature, IAsyncDisposable
    {
        public string Name => nameof(AsyncDisposableFeature);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
