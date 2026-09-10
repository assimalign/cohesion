using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Scheduler;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Scheduler.Cron.Tests;

public sealed class CronScheduleTests
{
    [Fact(DisplayName = "Cohesion Test [Scheduler.Cron] - RunAsync: evaluates and executes an occurrence")]
    public async Task RunAsync_WhenOccurrenceArrives_ShouldExecuteJobWithContext()
    {
        using var cancellation = new CancellationTokenSource();
        var clock = new ImmediateTimeProvider(
            new DateTimeOffset(2028, 1, 1, 0, 0, 30, TimeSpan.Zero));
        IScheduleContext? observed = null;
        CancellationToken occurrenceToken = new(canceled: true);
        var job = new TestJob((context, token) =>
        {
            observed = context;
            occurrenceToken = token;
            cancellation.Cancel();
            return ValueTask.CompletedTask;
        });
        var builder = new RecordingBuilder();
        builder.AddCronSchedule("every-minute", Crontab.Parse("* * * * *"), job, clock);
        ISchedule schedule = builder.Provider!.GetSchedules().Single();

        await schedule.RunAsync(cancellation.Token);

        observed.ShouldNotBeNull();
        observed.ScheduledTime.ShouldBe(new DateTime(2028, 1, 1, 0, 1, 0, DateTimeKind.Unspecified));
        occurrenceToken.CanBeCanceled.ShouldBeFalse();
        schedule.LastRunTime.ShouldBe(observed.ScheduledTime);
        schedule.Status.ShouldBe(ScheduleStatus.Stopped);
    }

    [Fact(DisplayName = "Cohesion Test [Scheduler.Cron] - RunAsync: cancellation stops before an occurrence")]
    public async Task RunAsync_WithPreCancelledToken_ShouldStopWithoutExecuting()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var job = new TestJob(static (_, _) => throw new InvalidOperationException("Must not execute."));
        var builder = new RecordingBuilder();
        builder.AddCronSchedule("* * * * *", job);
        ISchedule schedule = builder.Provider!.GetSchedules().Single();

        await schedule.RunAsync(cancellation.Token);

        schedule.Status.ShouldBe(ScheduleStatus.Stopped);
        schedule.LastRunTime.ShouldBeNull();
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

    private sealed class ImmediateTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            _utcNow += dueTime;
            ThreadPool.QueueUserWorkItem(_ => callback(state));
            return new ImmediateTimer();
        }
    }

    private sealed class ImmediateTimer : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
