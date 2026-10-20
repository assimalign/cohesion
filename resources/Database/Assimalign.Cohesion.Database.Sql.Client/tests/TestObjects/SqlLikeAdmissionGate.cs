using System;
using System.Diagnostics.Tracing;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Sql.Client.Tests;

/// <summary>Holds one admitted statement until its test disconnects the client.</summary>
internal sealed class SqlLikeAdmissionGate : EventListener
{
    private readonly EventSource _source;
    private readonly ManualResetEventSlim _release = new();
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal SqlLikeAdmissionGate(EventSource source)
    {
        _source = source;
        EnableEvents(source, EventLevel.Verbose, EventKeywords.All);
    }

    internal Task Started => _started.Task;

    internal void Release() => _release.Set();

    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        if (ReferenceEquals(eventData.EventSource, _source) && eventData.EventName == "StatementStart")
        {
            _started.TrySetResult();
            _release.Wait(TimeSpan.FromSeconds(20));
        }
    }

    public override void Dispose()
    {
        _release.Set();
        base.Dispose();
        _release.Dispose();
    }
}
