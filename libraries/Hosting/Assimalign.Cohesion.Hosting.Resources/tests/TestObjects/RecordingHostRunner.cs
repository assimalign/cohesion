using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.Hosting.Resources.Tests;

/// <summary>An inner runner that records what the configuration looked like when the host started.</summary>
internal sealed class RecordingHostRunner : IHostRunner
{
    private readonly Func<CancellationToken, Task> _running;
    private readonly Action? _started;

    public RecordingHostRunner(Func<CancellationToken, Task> running, Action? started = null)
    {
        _running = running;
        _started = started;
    }

    public int Runs { get; private set; }

    public async Task RunAsync(IHostRun run, CancellationToken cancellationToken = default)
    {
        Runs++;
        _started?.Invoke();
        await _running(cancellationToken).ConfigureAwait(false);
    }
}
