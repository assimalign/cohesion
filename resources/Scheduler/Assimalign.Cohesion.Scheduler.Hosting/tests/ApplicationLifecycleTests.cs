using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Core;
using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Hosting.Resources;
using Assimalign.Cohesion.Scheduler;
using Assimalign.Cohesion.Scheduler.Timer;

namespace Assimalign.Cohesion.Scheduler.Hosting.Tests;

public class ApplicationLifecycleTests
{
    [Fact(DisplayName = "Cohesion Test [Scheduler.Hosting] - AddService: Should honor mixed registration lifecycle order")]
    public async Task AddService_WithInstanceAndFactory_ShouldStartInRegistrationOrderAndStopInReverseOrder()
    {
        // Arrange
        List<string> events = [];
        RecordingService firstService = new("first", events);
        RecordingService secondService = new("second", events);
        SchedulerApplicationBuilder builder = SchedulerApplication.CreateBuilder([]);
        SchedulerApplicationContext? factoryContext = null;
        var factoryCount = 0;

        builder
            .AddService(firstService)
            .AddService(context =>
            {
                factoryCount++;
                factoryContext = context;
                return secondService;
            });

        await using SchedulerApplication application = ((ISchedulerApplicationBuilder)builder).Build().ShouldBeOfType<SchedulerApplication>();

        // Act
        await ((ISchedulerApplication)application).StartAsync(CancellationToken.None);
        await ((ISchedulerApplication)application).StopAsync(CancellationToken.None);

        // Assert
        factoryCount.ShouldBe(1);
        factoryContext.ShouldBeSameAs(application.Context);
        ((ISchedulerApplication)application).Context.ShouldBeSameAs(application.Context);
        ((ISchedulerApplication)application).Context.ContentRootPath.ShouldBe(application.Context.Environment.ContentRootPath);
        application.Context.HostedServices.Take(2).ShouldBe(new IHostService[] { firstService, secondService });
        application.Context.HostedServices.Count().ShouldBe(3);
        events.ShouldBe(new[] { "first:start", "second:start", "second:stop", "first:stop" });
    }

    [Fact(DisplayName = "Cohesion Test [Scheduler.Hosting] - AddService: Null registrations should fail explicitly")]
    public void AddService_WithNullRegistration_ShouldRejectRegistration()
    {
        // Arrange
        SchedulerApplicationBuilder builder = SchedulerApplication.CreateBuilder([]);

        // Act and assert
        Should.Throw<ArgumentNullException>(() => builder.AddService((IHostService)null!));
        Should.Throw<ArgumentNullException>(() => builder.AddService(
            (Func<SchedulerApplicationContext, IHostService>)null!));
    }

    [Fact(DisplayName = "Cohesion Test [Scheduler.Hosting] - AddScheduleProvider: rejects the same provider instance twice")]
    public void AddScheduleProvider_WithDuplicateInstance_ShouldThrow()
    {
        SchedulerApplicationBuilder builder = SchedulerApplication.CreateBuilder([]);
        var provider = new TestScheduleProvider(new TestSchedule());
        builder.AddScheduleProvider(provider);

        InvalidOperationException error = Should.Throw<InvalidOperationException>(
            () => builder.AddScheduleProvider(provider));

        error.Message.ShouldContain("same scheduler provider instance", Case.Insensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Scheduler.Hosting] - Build: rejects one schedule returned by multiple providers")]
    public void Build_WithDuplicateScheduleInstance_ShouldThrow()
    {
        SchedulerApplicationBuilder builder = SchedulerApplication.CreateBuilder([]);
        var schedule = new TestSchedule();
        builder.AddScheduleProvider(new TestScheduleProvider(schedule));
        builder.AddScheduleProvider(new TestScheduleProvider(schedule));

        InvalidOperationException error = Should.Throw<InvalidOperationException>(() => builder.Build());

        error.Message.ShouldContain("returned more than once", Case.Insensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Scheduler.Hosting] - Execution: first schedule fault cancels sibling loops")]
    public async Task Execution_WithFaultedSchedule_ShouldCancelSiblingAndPropagatePromptly()
    {
        var failure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var siblingCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var expected = new InvalidOperationException("schedule failed");
        var failingSchedule = new TestSchedule(_ => failure.Task);
        var siblingSchedule = new TestSchedule(async cancellationToken =>
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                siblingCancelled.TrySetResult();
            }
        });
        SchedulerApplicationBuilder builder = SchedulerApplication.CreateBuilder([]);
        builder.AddScheduleProvider(new TestScheduleProvider(failingSchedule, siblingSchedule));
        await using SchedulerApplication application = builder.Build();
        await ((IHost)application).StartAsync();

        failure.TrySetException(expected);
        await siblingCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        InvalidOperationException observed = await Should.ThrowAsync<InvalidOperationException>(
            () => ((IHost)application).StopAsync().WaitAsync(TimeSpan.FromSeconds(5)));

        observed.ShouldBeSameAs(expected);
    }

    [Fact(DisplayName = "Cohesion Test [Scheduler.Hosting] - Build: Null service factory result should fail explicitly")]
    public void Build_WithNullServiceFactoryResult_ShouldRejectService()
    {
        // Arrange
        SchedulerApplicationBuilder builder = SchedulerApplication.CreateBuilder([]);
        builder.AddService(_ => null!);

        // Act
        InvalidOperationException exception = Should.Throw<InvalidOperationException>(() => builder.Build());

        // Assert
        exception.Message.ShouldContain("service factory returned null", Case.Insensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Scheduler] - RunAsync: pre-cancelled token starts and stops once")]
    public async Task RunAsync_WhenCancellationIsRequestedBeforeStart_ShouldStartAndStopOnce()
    {
        // Arrange
        var events = new List<string>();
        SchedulerApplicationBuilder builder = SchedulerApplication.CreateBuilder([]);
        builder.AddService(new RecordingService("service", events));
        await using SchedulerApplication application = builder.Build();
        using var cancellationTokenSource = new CancellationTokenSource();
        cancellationTokenSource.Cancel();

        // Act
        await application.RunAsync(cancellationTokenSource.Token).WaitAsync(TimeSpan.FromSeconds(5));

        // Assert
        events.ShouldBe(new[] { "service:start", "service:stop" });
        application.Context.State.ShouldBe(HostState.Stopped);
    }

    [Fact(DisplayName = "Cohesion Test [Scheduler.Hosting] - Lifecycle: stop drains an active occurrence")]
    public async Task StopAsync_WithActiveOccurrence_ShouldWaitForOccurrence()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        SchedulerApplicationBuilder builder = SchedulerApplication.CreateBuilder([]);
        IScheduleJob job = builder.AddJob("blocking", async (_, token) =>
        {
            token.CanBeCanceled.ShouldBeFalse();
            started.TrySetResult();
            await release.Task.ConfigureAwait(false);
        });
        builder.AddTimerSchedule(
            "blocking-timer",
            TimeSpan.Zero,
            TimeSpan.FromHours(1),
            job,
            TimeProvider.System);
        await using SchedulerApplication application = builder.Build();
        await ((IHost)application).StartAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        Task stopping = ((IHost)application).StopAsync(grace.Token);
        await Task.Delay(50);

        stopping.IsCompleted.ShouldBeFalse();
        release.TrySetResult();
        await stopping;
        application.Context.State.ShouldBe(HostState.Stopped);
    }

    [Fact(DisplayName = "Cohesion Test [Scheduler.Hosting] - Lifecycle: stop bounds an occurrence that exceeds grace")]
    public async Task StopAsync_WithOccurrenceExceedingGrace_ShouldCompleteWithinCallerBudget()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        SchedulerApplicationBuilder builder = SchedulerApplication.CreateBuilder([]);
        IScheduleJob job = builder.AddJob("unbounded", async (_, _) =>
        {
            started.TrySetResult();
            await release.Task.ConfigureAwait(false);
        });
        builder.AddTimerSchedule(
            "unbounded-timer",
            TimeSpan.Zero,
            TimeSpan.FromHours(1),
            job,
            TimeProvider.System);
        await using SchedulerApplication application = builder.Build();
        await ((IHost)application).StartAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        try
        {
            using var grace = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await Should.ThrowAsync<OperationCanceledException>(
                () => ((IHost)application).StopAsync(grace.Token).WaitAsync(TimeSpan.FromSeconds(5)));
            application.Context.State.ShouldBe(HostState.Stopped);
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [Fact(DisplayName = "Cohesion Test [Scheduler.Hosting] - AddJob: an unbound job remains dormant")]
    public async Task AddJob_WithoutSchedule_ShouldRemainDormant()
    {
        var executionCount = 0;
        SchedulerApplicationBuilder builder = SchedulerApplication.CreateBuilder([]);
        builder.AddJob("dormant", (_, _) =>
        {
            Interlocked.Increment(ref executionCount);
            return ValueTask.CompletedTask;
        });
        await using SchedulerApplication application = builder.Build();

        await ((IHost)application).StartAsync();
        await Task.Delay(25);
        await ((IHost)application).StopAsync();

        executionCount.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Scheduler.Hosting] - Build: rejects schedules bound to undeclared jobs")]
    public void Build_WithUndeclaredScheduledJob_ShouldThrow()
    {
        SchedulerApplicationBuilder builder = SchedulerApplication.CreateBuilder([]);
        var job = new TestJob();
        builder.AddTimerSchedule(TimeSpan.FromMinutes(1), job);

        InvalidOperationException error = Should.Throw<InvalidOperationException>(() => builder.Build());

        error.Message.ShouldContain("declared with AddJob", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Scheduler.Hosting] - Control plane: serves probes and authenticated management")]
    public async Task ControlPlane_WithAmbientHttpEndpoint_ShouldServeResourceRoutes()
    {
        int port = GetAvailablePort();
        var endpoint = new Uri($"http://127.0.0.1:{port}");
        using var identity = new TestBootstrapIdentity("tests", "test-gateway");
        string credential = identity.Issue("scheduler");
        Assembly entryAssembly = typeof(ApplicationLifecycleTests).Assembly;
        ResourceRuntime.RegisterControlPlane(
            entryAssembly,
            static () => ResourceControlPlane.Create());
        var untrustedResourceContext = new ResourceContext(
            applicationName: "tests",
            resourceName: "scheduler",
            environmentName: "Development",
            gatewayName: null,
            contentRootPath: null,
            endpoints: new Dictionary<string, Uri> { ["http"] = endpoint },
            mounts: null,
            settings: null,
            references: null,
            bootstrapCredential: ReadOnlyMemory<byte>.Empty,
            applicationTrustKey: identity.PublicKey,
            ambientValues: null);
        using (ResourceRuntime.CreateScope(untrustedResourceContext))
        {
            SchedulerApplicationBuilder untrustedBuilder = CreateResourceBuilder(entryAssembly);
            InvalidOperationException error = Should.Throw<InvalidOperationException>(
                () => untrustedBuilder.Build());
            error.Message.ShouldContain("gateway identity", Case.Insensitive);
        }

        var resourceContext = new ResourceContext(
            applicationName: "tests",
            resourceName: "scheduler",
            environmentName: "Development",
            gatewayName: "test-gateway",
            contentRootPath: null,
            endpoints: new Dictionary<string, Uri> { ["http"] = endpoint },
            mounts: null,
            settings: null,
            references: null,
            bootstrapCredential: Encoding.UTF8.GetBytes(credential),
            applicationTrustKey: identity.PublicKey,
            ambientValues: null);

        using (ResourceRuntime.CreateScope(resourceContext))
        {
            SchedulerApplicationBuilder builder = CreateResourceBuilder(entryAssembly);
            await using SchedulerApplication application = builder.Build();
            await ((IHost)application).StartAsync();
            using var client = new HttpClient { BaseAddress = endpoint };

            HttpResponseMessage health = await client.GetAsync("/healthz");
            HttpResponseMessage unauthorized = await client.GetAsync("/cohesion/v1/endpoints");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                credential);
            HttpResponseMessage endpoints = await client.GetAsync("/cohesion/v1/endpoints");
            HttpResponseMessage stop = await client.PostAsync("/cohesion/v1/stop", content: null);

            health.StatusCode.ShouldBe(HttpStatusCode.OK);
            unauthorized.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            endpoints.StatusCode.ShouldBe(HttpStatusCode.OK);
            string endpointDocument = await endpoints.Content.ReadAsStringAsync();
            endpointDocument.ShouldContain(endpoint.ToEndpointString());
            endpointDocument.ShouldNotContain(endpoint.ToString());
            stop.StatusCode.ShouldBe(HttpStatusCode.Accepted);
            await ((IHost)application).StopAsync();
        }
    }

    private sealed class RecordingService : IHostService
    {
        private readonly string _name;
        private readonly ICollection<string> _events;

        /// <summary>Initializes a new instance of the <see cref="RecordingService"/> class.</summary>
        /// <param name="name">The name recorded with each lifecycle event.</param>
        /// <param name="events">The collection that receives the recorded lifecycle events.</param>
        public RecordingService(
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

    private sealed class TestJob : IScheduleJob
    {
        public JobId Id { get; } = JobId.New();

        public string? Name => "undeclared";

        public JobState State => JobState.Enabled;

        public ValueTask ExecuteAsync(
            IScheduleContext context,
            CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }

    private sealed class TestScheduleProvider : IScheduleProvider
    {
        private readonly ISchedule[] _schedules;

        /// <summary>Initializes a new instance of the <see cref="TestScheduleProvider"/> class.</summary>
        /// <param name="schedules">The schedules the provider exposes.</param>
        public TestScheduleProvider(params ISchedule[] schedules)
        {
            _schedules = schedules;
        }

        public ISchedule GetSchedule(ScheduleId id) =>
            _schedules.Single(schedule => schedule.Id == id);

        public IEnumerable<ISchedule> GetSchedules() => _schedules;

        public void DisableJob(ScheduleId scheduleId, JobId jobId) =>
            throw new NotSupportedException();

        public void EnableJob(ScheduleId scheduleId, JobId jobId) =>
            throw new NotSupportedException();

        public bool IsJobEnabled(ScheduleId scheduleId, JobId jobId) => true;
    }

    private sealed class TestSchedule : ISchedule
    {
        private readonly Func<CancellationToken, Task>? _run;

        /// <summary>Initializes a new instance of the <see cref="TestSchedule"/> class.</summary>
        /// <param name="run">The optional delegate invoked when the schedule runs; <see langword="null"/> completes immediately.</param>
        public TestSchedule(Func<CancellationToken, Task>? run = null)
        {
            _run = run;
        }

        public ScheduleId Id { get; } = ScheduleId.New();

        public string? Name => "test";

        public string? Description => null;

        public int Retries => 0;

        public int[] RetryIntervals => [];

        public int RetryCount => 0;

        public DateTime? NextRunTime => null;

        public DateTime? LastRunTime => null;

        public ScheduleStatus Status => ScheduleStatus.Idle;

        public SchedulePriority Priority => SchedulePriority.Normal;

        public IEnumerable<IScheduleJob> Jobs => [];

        public Task RunAsync(CancellationToken cancellationToken = default) =>
            _run?.Invoke(cancellationToken) ?? Task.CompletedTask;
    }

    private static SchedulerApplicationBuilder CreateResourceBuilder(Assembly resourceAssembly)
    {
        MethodInfo createBuilder = typeof(SchedulerApplication).GetMethod(
            "CreateBuilder",
            BindingFlags.Static | BindingFlags.NonPublic,
            binder: null,
            [typeof(string[]), typeof(Assembly)],
            modifiers: null)
            ?? throw new InvalidOperationException("The internal resource builder overload was not found.");
        return (SchedulerApplicationBuilder)(createBuilder.Invoke(
            obj: null,
            parameters: [Array.Empty<string>(), resourceAssembly])
            ?? throw new InvalidOperationException("The internal resource builder returned null."));
    }

    private static int GetAvailablePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
