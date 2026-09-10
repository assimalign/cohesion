using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Scheduler;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Scheduler.Timer.Tests;

public sealed class TimerScheduleTests
{
    [Fact(DisplayName = "Cohesion Test [Scheduler.Timer] - RunAsync: executes a due occurrence")]
    public async Task RunAsync_WhenDue_ShouldExecuteOccurrence()
    {
        using var cancellation = new CancellationTokenSource();
        IScheduleContext? observed = null;
        var job = new TestJob((context, token) =>
        {
            observed = context;
            token.CanBeCanceled.ShouldBeFalse();
            cancellation.Cancel();
            return ValueTask.CompletedTask;
        });
        var builder = new RecordingBuilder();
        builder.AddTimerSchedule(
            "heartbeat",
            TimeSpan.Zero,
            TimeSpan.FromMinutes(1),
            job,
            TimeProvider.System);
        ISchedule schedule = builder.Provider!.GetSchedules().Single();

        await schedule.RunAsync(cancellation.Token);

        observed.ShouldNotBeNull();
        observed.Name.ShouldBe("heartbeat");
        schedule.LastRunTime.ShouldBe(observed.ScheduledTime);
        schedule.Status.ShouldBe(ScheduleStatus.Stopped);
    }

    [Fact(DisplayName = "Cohesion Test [Scheduler.Timer] - AddTimerSchedule: rejects invalid intervals")]
    public void AddTimerSchedule_WithNonPositiveInterval_ShouldThrow()
    {
        var builder = new RecordingBuilder();
        var job = new TestJob(static (_, _) => ValueTask.CompletedTask);

        Should.Throw<ArgumentOutOfRangeException>(
            () => builder.AddTimerSchedule(TimeSpan.Zero, job));
    }

    private sealed class RecordingBuilder : ISchedulerApplicationBuilder
    {
        public IScheduleProvider? Provider { get; private set; }

        public ISchedulerApplicationBuilder AddJob(IScheduleJob job) => this;

        public ISchedulerApplicationBuilder AddScheduleProvider(IScheduleProvider provider)
        {
            Provider = provider;
            return this;
        }

        public ISchedulerApplicationBuilder AddService(IHostService service) => this;

        public ISchedulerApplicationBuilder AddService(Func<IHostContext, IHostService> factory) => this;

        public ISchedulerApplication Build() => throw new NotSupportedException();

        IHost IHostBuilder.Build() => Build();
    }

    private sealed class TestJob(
        Func<IScheduleContext, CancellationToken, ValueTask> execute) : IScheduleJob
    {
        public JobId Id { get; } = JobId.New();

        public string? Name => "test";

        public JobState State => JobState.Enabled;

        public ValueTask ExecuteAsync(
            IScheduleContext context,
            CancellationToken cancellationToken = default) =>
            execute(context, cancellationToken);
    }
}
