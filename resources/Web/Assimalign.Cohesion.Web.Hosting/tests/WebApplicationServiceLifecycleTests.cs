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

        builder.AddService(firstService);
        ((IWebApplicationBuilder)builder).AddServer(server);
        builder.AddService(context =>
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
            (Func<WebApplicationContext, IHostService>)null!));
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

    private sealed class RecordingHostService : IHostService
    {
        private readonly string _name;
        private readonly ICollection<string> _events;

        /// <summary>
        /// Initializes a new instance of the <see cref="RecordingHostService"/> class.
        /// </summary>
        /// <param name="name">The name that prefixes each recorded lifecycle event.</param>
        /// <param name="events">The collection that receives the recorded lifecycle events.</param>
        public RecordingHostService(
            string name,
            ICollection<string> events)
        {
            _name = name;
            _events = events;
        }

        public ServiceId Id { get; } = ServiceId.New();

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
        private readonly ICollection<string> _events;

        /// <summary>
        /// Initializes a new instance of the <see cref="RecordingApplicationServer"/> class.
        /// </summary>
        /// <param name="events">The collection that receives the recorded lifecycle events.</param>
        public RecordingApplicationServer(
            ICollection<string> events)
        {
            _events = events;
        }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _events.Add("server:start");
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            _events.Add("server:stop");
            return Task.CompletedTask;
        }
    }
}
