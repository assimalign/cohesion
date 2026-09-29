using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.DependencyInjection;
using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Http.Connections;
using Assimalign.Cohesion.Web;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Web.Hosting.Tests;

public class WebApplicationDependencyInjectionTests
{
    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Dependency injection: Registration should close when the application is built")]
    public async Task Build_ShouldCloseServiceRegistration()
    {
        // Arrange
        WebApplicationBuilder builder = WebApplication.CreateBuilder();

        // Act
        await using WebApplication application = builder.Build();

        // Assert
        Should.Throw<InvalidOperationException>(() => builder.Services.AddSingleton<IHostService>(
            new RecordingHostService("late", new List<string>())));
        Should.Throw<InvalidOperationException>(() => builder.AddService(
            new RecordingHostService("late", new List<string>())));
        Should.Throw<InvalidOperationException>(() => builder.Build());
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Dependency injection: Direct registrations should join the lifecycle phase their service type names")]
    public async Task Services_WithDirectRegistrations_ShouldJoinTheirLifecyclePhase()
    {
        // Arrange
        List<string> events = new();
        WebApplicationBuilder builder = WebApplication.CreateBuilder();

        builder.Services.AddSingleton<IWebApplicationServer>(new RecordingApplicationServer("server", events));
        builder.Services.AddSingleton<IHostService>(new RecordingHostService("service", events));

        await using WebApplication application = builder.Build();

        // Act
        await ((IWebApplication)application).StartAsync();
        await ((IWebApplication)application).StopAsync();

        // Assert
        events.ShouldBe(new[]
        {
            "service:start",
            "server:start",
            "server:stop",
            "service:stop",
        });
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Dependency injection: Disposal should release factory-created services and leave instances with their callers")]
    public async Task DisposeAsync_ShouldDisposeOnlyFactoryCreatedServices()
    {
        // Arrange
        DisposableHostService owned = new();
        DisposableHostService borrowed = new();
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.AddService(borrowed);
        builder.AddService(_ => owned);
        WebApplication application = builder.Build();

        // Act
        await ((IAsyncDisposable)application).DisposeAsync();

        // Assert
        owned.DisposeCount.ShouldBe(1);
        borrowed.DisposeCount.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Dependency injection: A failed build should dispose the services it already created")]
    public void Build_WithFailingServiceFactory_ShouldDisposeCreatedServices()
    {
        // Arrange
        DisposableHostService created = new();
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.AddService(_ => created);
        builder.AddService(_ => throw new InvalidOperationException("factory failure"));

        // Act
        InvalidOperationException exception = Should.Throw<InvalidOperationException>(() => builder.Build());

        // Assert
        exception.Message.ShouldBe("factory failure");
        created.DisposeCount.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Dependency injection: A feature factory should run once, after the application is built")]
    public async Task AddFeature_WithFactory_ShouldResolveOnceAgainstTheBuiltContext()
    {
        // Arrange
        int calls = 0;
        IWebApplicationContext? observed = null;
        TestFeature feature = new();
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        ((IWebApplicationBuilder)builder).AddFeature(context =>
        {
            calls++;
            observed = context;
            return feature;
        });

        // Act
        int callsBeforeBuild = calls;
        await using WebApplication application = builder.Build();

        // Assert
        callsBeforeBuild.ShouldBe(0);
        application.Context.Features.ShouldContain(feature);
        application.Context.Features.ShouldContain(feature);
        calls.ShouldBe(1);
        observed.ShouldBeSameAs(application.Context);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Dependency injection: A pipeline passed to AddPipeline should stay with its caller")]
    public async Task AddPipeline_WithDisposablePipeline_ShouldNotDisposeItWithTheApplication()
    {
        // Arrange
        DisposablePipeline pipeline = new();
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Server.UseServer(options => options.UseHttp1(
            tcp => tcp.EndPoint = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0)));
        ((IWebApplicationBuilder)builder).AddPipeline(pipeline);
        WebApplication application = builder.Build();

        // Act
        await ((IWebApplication)application).StartAsync();
        await ((IWebApplication)application).StopAsync();
        await ((IAsyncDisposable)application).DisposeAsync();

        // Assert
        pipeline.DisposeCount.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Dependency injection: Root verbs should reject null registrations")]
    public void RootVerbs_WithNullRegistrations_ShouldRejectRegistration()
    {
        IWebApplicationBuilder builder = WebApplication.CreateBuilder();

        Should.Throw<ArgumentNullException>(() => builder.AddFeature((IHttpFeature)null!));
        Should.Throw<ArgumentNullException>(() => builder.AddFeature(
            (Func<IWebApplicationContext, IHttpFeature>)null!));
        Should.Throw<ArgumentNullException>(() => builder.AddServer((IWebApplicationServer)null!));
        Should.Throw<ArgumentNullException>(() => builder.AddServer(
            (Func<IWebApplicationContext, IWebApplicationServer>)null!));
    }

    private sealed class RecordingHostService : IHostService
    {
        private readonly string _name;
        private readonly ICollection<string> _events;

        public RecordingHostService(string name, ICollection<string> events)
        {
            _name = name;
            _events = events;
        }

        public ServiceId Id { get; } = ServiceId.New();

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            _events.Add($"{_name}:start");
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            _events.Add($"{_name}:stop");
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingApplicationServer : IWebApplicationServer
    {
        private readonly string _name;
        private readonly ICollection<string> _events;

        public RecordingApplicationServer(string name, ICollection<string> events)
        {
            _name = name;
            _events = events;
        }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            _events.Add($"{_name}:start");
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            _events.Add($"{_name}:stop");
            return Task.CompletedTask;
        }
    }

    private sealed class DisposableHostService : IHostService, IDisposable
    {
        public ServiceId Id { get; } = ServiceId.New();

        public int DisposeCount { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public void Dispose() => DisposeCount++;
    }

    private sealed class TestFeature : IHttpFeature
    {
        public string Name => nameof(TestFeature);
    }

    private sealed class DisposablePipeline : IWebApplicationPipeline, IDisposable
    {
        public int DisposeCount { get; private set; }

        public Task ExecuteAsync(IHttpContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public void Dispose() => DisposeCount++;
    }
}
