using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.NatGateway.Hosting.Internal;

using Assimalign.Cohesion.Hosting;

// Implements IHostService directly per the execution menu: the data plane owns one thread
// per core for its entire life, which neither the pooled base (work migrates to the pool)
// nor the single dedicated-thread base (one thread, not N) models. See docs/DESIGN.md.
internal sealed class TranslationDataPlaneService : IHostService
{
    private CancellationTokenSource? _cancellationTokenSource;
    private TaskCompletionSource? _exited;
    private Exception? _fault;
    private int _running;

    public ServiceId Id { get; } = ServiceId.New();

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        var cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var token = cancellationTokenSource.Token;
        var threadCount = Environment.ProcessorCount;

        _cancellationTokenSource = cancellationTokenSource;
        _exited = exited;
        _fault = null;
        _running = threadCount;

        for (var index = 0; index < threadCount; index++)
        {
            var core = index;
            var thread = new Thread(() =>
            {
                try
                {
                    Run(core, token);
                }
                catch (OperationCanceledException)
                {
                    // Cooperative exit in response to the stop signal.
                }
                catch (Exception exception)
                {
                    // First fault wins; surfaced on the join once the last loop exits.
                    Interlocked.CompareExchange(ref _fault, exception, null);
                }
                finally
                {
                    if (Interlocked.Decrement(ref _running) == 0)
                    {
                        Exception? fault = _fault;

                        if (fault is null)
                        {
                            exited.TrySetResult();
                        }
                        else
                        {
                            exited.TrySetException(fault);
                        }
                    }
                }
            })
            {
                IsBackground = true,
                Name = $"TranslationDataPlaneService:{core}",
            };

            thread.Start();
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        TaskCompletionSource? exited = _exited;

        if (exited is null)
        {
            return;
        }

        _cancellationTokenSource!.Cancel();

        try
        {
            await exited.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (exited.Task.IsCompleted)
            {
                _cancellationTokenSource.Dispose();
                _cancellationTokenSource = null;
                _exited = null;
            }
        }
    }

    private void Run(int core, CancellationToken cancellationToken)
    {
        // TODO: pin and run this core's loop to translate and forward packets. The placeholder blocks until the
        // host stops so the scaffolded application starts and drains cleanly.
        cancellationToken.WaitHandle.WaitOne();
    }
}