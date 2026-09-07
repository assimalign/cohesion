using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Hosting;

internal sealed class ResourceHostRunner : IHostRunner
{
    internal ResourceHostRunner(ResourceHostOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        Options = options;
    }

    internal ResourceHostOptions Options { get; }

    public async Task RunAsync(
        IHostRun run,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);

        bool isProcessRun = Options.RunMode is ResourceHostRunMode.Process;
        var runState = new ResourceHost.RunState(
            run,
            isProcessRun ? Options.ProtocolLineWriter : null);
        int exitCode;

        try
        {
            run.ShutdownTimeout = Options.ShutdownTimeout;
            ResourceHost.AssertContentRoot(
                run.Host.Context.Environment,
                Options.ContentRootPath);

            using IDisposable? signalSubscription = isProcessRun
                ? Options.SignalSource.Subscribe(
                    runState.RequestShutdown,
                    Options.StopEventName)
                : null;

            await run.RunAsync(runState, cancellationToken).ConfigureAwait(false);

            runState.ThrowIfProtocolLineFailed();
            exitCode = runState.IsDrainAborted
                ? ResourceHost.GetDrainAbortExitCode(runState.StopSignal)
                : ResourceHost.SuccessExitCode;
        }
        catch (Exception exception) when (isProcessRun)
        {
            // ResourceHost is the executable boundary: every host failure is converted
            // to the frozen sysexits/v1 contract instead of escaping as a platform-
            // dependent unhandled-exception exit code.
            exitCode = ResourceHost.ClassifyExitCode(
                exception,
                Options,
                runState.HasReachedReady,
                runState.IsDrainAborted,
                runState.StopSignal);
        }

        if (isProcessRun)
        {
            Options.ExitCodeHandler(exitCode);
        }
    }
}
