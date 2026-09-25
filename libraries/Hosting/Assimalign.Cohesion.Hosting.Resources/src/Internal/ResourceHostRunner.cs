using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Hosting.Resources.Internal;

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

            Task runTask = run.RunAsync(runState, cancellationToken);
            Options.RunInvoked?.Invoke();
            await runTask.ConfigureAwait(false);

            runState.ThrowIfProtocolLineFailed();
            exitCode = runState.IsDrainAborted
                ? ResourceHost.GetDrainAbortExitCode(runState.StopSignal)
                : ResourceHost.SuccessExitCode;
        }
        catch (Exception exception)
        {
            // ResourceHost is the executable boundary: every host failure is converted
            // to the frozen sysexits/v1 contract instead of escaping as a platform-
            // dependent unhandled-exception exit code.
            exitCode = exception is ResourceEntryExitException entryExit
                ? entryExit.ExitCode
                : ResourceHost.ClassifyExitCode(
                    exception,
                    Options,
                    runState.HasReachedReady,
                    runState.IsDrainAborted,
                    runState.StopSignal);

            if (!isProcessRun)
            {
                if (exception is ResourceEntryExitException)
                {
                    throw;
                }
                throw new ResourceEntryExitException(exitCode, exception);
            }
        }

        if (isProcessRun)
        {
            Options.ExitCodeHandler(exitCode);
            return;
        }

        if (exitCode != ResourceHost.SuccessExitCode)
        {
            throw new ResourceEntryExitException(exitCode);
        }
    }
}
