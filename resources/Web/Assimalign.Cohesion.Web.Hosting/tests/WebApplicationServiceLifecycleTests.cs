using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Web;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Web.Hosting.Tests;

public class WebApplicationServiceLifecycleTests
{
    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Application services: Should start before Web servers in registration order")]
    public async Task AddService_WithInterleavedInstanceAndFactory_ShouldRunBeforeServersInRegistrationOrder()
    {
        // Arrange
        List<string> events = new();
        RecordingHostService firstService = new("first", events);
        RecordingHostService secondService = new("second", events);
        RecordingApplicationServer server = new(events);
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        IWebApplicationContext? factoryContext = null;
        int factoryCount = 0;

        ((IWebApplicationBuilder)builder)
            .AddService(firstService)
            .AddServer(server)
            .AddService(context =>
            {
                factoryCount++;
                factoryContext = context;
                return secondService;
            });

        await using WebApplication application = builder.Build();

        // Act
        await ((IWebApplication)application).StartAsync();
        await ((IWebApplication)application).StopAsync();

        // Assert
        factoryCount.ShouldBe(1);
        factoryContext.ShouldBeSameAs(application.Context);
        application.Context.HostedServices.Take(2).ShouldBe(
            new IHostService[] { firstService, secondService });
        events.ShouldBe(new[]
        {
            "first:start",
            "second:start",
            "server:start",
            "server:stop",
            "second:stop",
            "first:stop",
        });
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Application services: Null registrations should fail explicitly")]
    public void AddService_WithNullRegistration_ShouldRejectRegistration()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();

        Should.Throw<ArgumentNullException>(() => builder.AddService((IHostService)null!));
        Should.Throw<ArgumentNullException>(() => builder.AddService(
            (Func<IWebApplicationContext, IHostService>)null!));
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Application services: Null factory result should fail at build")]
    public void Build_WithNullServiceFactoryResult_ShouldRejectService()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.AddService(_ => null!);

        InvalidOperationException exception = Should.Throw<InvalidOperationException>(
            () => builder.Build());

        exception.Message.ShouldContain("service factory returned null", Case.Insensitive);
    }

    private sealed class RecordingHostService(
        string name,
        ICollection<string> events) : IHostService
    {
        public ServiceId Id { get; } = ServiceId.New();

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            events.Add($"{name}:start");
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            events.Add($"{name}:stop");
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingApplicationServer(
        ICollection<string> events) : IWebApplicationServer
    {
        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            events.Add("server:start");
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            events.Add("server:stop");
            return Task.CompletedTask;
        }
    }
}
