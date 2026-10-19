using System;
using System.Diagnostics.Tracing;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Documents.Client.Internal;

namespace Assimalign.Cohesion.Database.Documents.Client.Tests;

internal sealed class PausedQueryListener : EventListener
{
    private readonly ManualResetEventSlim _resume = new();
    private readonly TaskCompletionSource _paused = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationToken _token;
    private int _pauseCount;

    internal PausedQueryListener(CancellationToken token)
    {
        _token = token;
        EnableEvents(DocumentClientEventSource.Log, EventLevel.Verbose, (EventKeywords)1);
    }

    internal Task Paused => _paused.Task;
    internal int PauseCount => Volatile.Read(ref _pauseCount);
    internal void Resume() => _resume.Set();

    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        if (eventData.EventId == 1 && eventData.Payload?[1] is "Put")
        {
            Interlocked.Increment(ref _pauseCount);
            _paused.TrySetResult();
            _resume.Wait(_token);
        }
    }

    public override void Dispose()
    {
        _resume.Set();
        base.Dispose();
        _resume.Dispose();
    }
}
