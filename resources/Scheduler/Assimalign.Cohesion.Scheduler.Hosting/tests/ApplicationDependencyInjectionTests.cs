using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.DependencyInjection;
using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.Scheduler.Hosting.Tests;

public class ApplicationDependencyInjectionTests
{
    [Fact(DisplayName = "Cohesion Test [Scheduler] - Services: Registration should close when the application is built")]
    public async Task Build_ShouldCloseServiceRegistration()
    {
        // Arrange
        SchedulerApplicationBuilder builder = SchedulerApplication.CreateBuilder([]);

        // Act
        await using SchedulerApplication application = builder.Build();

        // Assert
        Should.Throw<InvalidOperationException>(() => builder.Services.AddSingleton<IHostService>(new DisposableHostService()));
        Should.Throw<InvalidOperationException>(() => builder.AddService(new DisposableHostService()));
        Should.Throw<InvalidOperationException>(() => builder.Build());
    }

    [Fact(DisplayName = "Cohesion Test [Scheduler] - Services: Direct IHostService registrations should join the lifecycle in registration order")]
    public async Task Services_WithDirectRegistration_ShouldJoinTheLifecycleInRegistrationOrder()
    {
        // Arrange
        var events = new List<string>();
        SchedulerApplicationBuilder builder = SchedulerApplication.CreateBuilder([]);
        builder.AddService(new RecordingHostService("first", events));
        builder.Services.AddSingleton<IHostService>(new RecordingHostService("second", events));
        builder.AddService(_ => new RecordingHostService("third", events));

        await using SchedulerApplication application = builder.Build();

        // Act
        await ((ISchedulerApplication)application).StartAsync(CancellationToken.None);
        await ((ISchedulerApplication)application).StopAsync(CancellationToken.None);

        // Assert
        events.ShouldBe(new[]
        {
            "first:start",
            "second:start",
            "third:start",
            "third:stop",
            "second:stop",
            "first:stop",
        });
    }

    [Fact(DisplayName = "Cohesion Test [Scheduler] - Services: Disposal should release factory-created services and leave instances with their callers")]
    public async Task DisposeAsync_ShouldDisposeOnlyFactoryCreatedServices()
    {
        // Arrange
        var owned = new DisposableHostService();
        var borrowed = new DisposableHostService();
        SchedulerApplicationBuilder builder = SchedulerApplication.CreateBuilder([]);
        builder.AddService(borrowed);
        builder.AddService(_ => owned);
        SchedulerApplication application = builder.Build();

        // Act
        await ((IAsyncDisposable)application).DisposeAsync();

        // Assert
        owned.DisposeCount.ShouldBe(1);
        borrowed.DisposeCount.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Scheduler] - Services: A failed build should dispose the services it already created")]
    public void Build_WithFailingServiceFactory_ShouldDisposeCreatedServices()
    {
        // Arrange
        var created = new DisposableHostService();
        SchedulerApplicationBuilder builder = SchedulerApplication.CreateBuilder([]);
        builder.AddService(_ => created);
        builder.AddService(_ => throw new InvalidOperationException("factory failure"));

        // Act
        InvalidOperationException exception = Should.Throw<InvalidOperationException>(() => builder.Build());

        // Assert
        exception.Message.ShouldBe("factory failure");
        created.DisposeCount.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Scheduler] - Services: Jobs and providers registered directly should compose like AddJob and AddScheduleProvider")]
    public async Task Services_WithDirectJobAndProviderRegistrations_ShouldComposeLikeRootVerbs()
    {
        // Arrange
        var job = new TestJob();
        var provider = new TestScheduleProvider();
        SchedulerApplicationBuilder builder = SchedulerApplication.CreateBuilder([]);
        builder.Services.AddSingleton<IScheduleJob>(job);
        builder.Services.AddSingleton<IScheduleProvider>(provider);

        // Act
        await using SchedulerApplication application = builder.Build();

        // Assert
        application.Context.Jobs.ShouldBe(new IScheduleJob[] { job });
        application.Context.ScheduleProviders.ShouldBe(new IScheduleProvider[] { provider });
    }

    [Fact(DisplayName = "Cohesion Test [Scheduler] - Services: Root verbs should register through the builder's services")]
    public async Task RootVerbs_ShouldRegisterThroughServices()
    {
        // Arrange
        var job = new TestJob();
        var provider = new TestScheduleProvider();
        ISchedulerApplicationBuilder builder = SchedulerApplication.CreateBuilder([]);

        // Act
        builder.AddJob(job).AddScheduleProvider(provider).AddJob(job);
        await using SchedulerApplication application = ((SchedulerApplicationBuilder)builder).Build();

        // Assert
        application.Context.Jobs.ShouldBe(new IScheduleJob[] { job });
        application.Context.ScheduleProviders.ShouldBe(new IScheduleProvider[] { provider });
    }

    [Fact(DisplayName = "Cohesion Test [Scheduler] - Services: AddJob should reject a different job whose identifier is already registered")]
    public void AddJob_WithConflictingRegisteredJob_ShouldThrow()
    {
        // Arrange
        var first = new TestJob();
        var conflicting = new TestJob(first.Id);
        SchedulerApplicationBuilder builder = SchedulerApplication.CreateBuilder([]);
        builder.Services.AddSingleton<IScheduleJob>(first);

        // Act
        InvalidOperationException exception = Should.Throw<InvalidOperationException>(() => builder.AddJob(conflicting));

        // Assert
        exception.Message.ShouldContain("already declared", Case.Insensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Scheduler] - Services: Two different jobs with one identifier should fail at build")]
    public void Build_WithConflictingDirectJobRegistrations_ShouldThrow()
    {
        // Arrange
        var first = new TestJob();
        var conflicting = new TestJob(first.Id);
        SchedulerApplicationBuilder builder = SchedulerApplication.CreateBuilder([]);
        builder.AddJob(first);
        builder.Services.AddSingleton<IScheduleJob>(conflicting);

        // Act
        InvalidOperationException exception = Should.Throw<InvalidOperationException>(() => builder.Build());

        // Assert
        exception.Message.ShouldContain("already declared", Case.Insensitive);
    }

    private sealed class TestJob : IScheduleJob
    {
        public TestJob(JobId? id = null)
        {
            Id = id ?? JobId.New();
        }

        public JobId Id { get; }

        public string? Name => "test";

        public JobState State => JobState.Enabled;

        public ValueTask ExecuteAsync(IScheduleContext context, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }

    private sealed class TestScheduleProvider : IScheduleProvider
    {
        public ISchedule GetSchedule(ScheduleId id) => throw new NotSupportedException();

        public IEnumerable<ISchedule> GetSchedules() => [];

        public void DisableJob(ScheduleId scheduleId, JobId jobId) => throw new NotSupportedException();

        public void EnableJob(ScheduleId scheduleId, JobId jobId) => throw new NotSupportedException();

        public bool IsJobEnabled(ScheduleId scheduleId, JobId jobId) => true;
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

    private sealed class DisposableHostService : IHostService, IDisposable
    {
        public ServiceId Id { get; } = ServiceId.New();

        public int DisposeCount { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public void Dispose() => DisposeCount++;
    }
}
