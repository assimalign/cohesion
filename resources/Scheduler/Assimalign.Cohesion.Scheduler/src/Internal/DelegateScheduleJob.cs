using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Scheduler.Internal;

[global::System.Diagnostics.DebuggerDisplay("DelegateScheduleJob {Name,nq}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
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

    private sealed class DebugView
    {
        private readonly DelegateScheduleJob _target;

        public DebugView(DelegateScheduleJob value)
        {
            _target = value;
        }

        public JobId Id => _target.Id;
        public string Name => _target.Name;
        public JobState State => _target.State;
    }
}
