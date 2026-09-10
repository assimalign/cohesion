using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Scheduler;

internal sealed class DelegateScheduleJob : IScheduleJob
{
    private readonly Func<IScheduleContext, CancellationToken, ValueTask> _execute;

    internal DelegateScheduleJob(
        JobId id,
        string name,
        Func<IScheduleContext, CancellationToken, ValueTask> execute)
    {
        Id = id;
        Name = name;
        _execute = execute;
    }

    public JobId Id { get; }

    public string Name { get; }

    public JobState State => JobState.Enabled;

    public ValueTask ExecuteAsync(
        IScheduleContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        return _execute.Invoke(context, cancellationToken);
    }
}
