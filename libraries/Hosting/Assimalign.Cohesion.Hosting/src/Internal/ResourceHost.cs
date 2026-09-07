using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Hosting;

internal static class ResourceHost
{
    internal const string ReadyProtocolLine = "cohesion-resource: ready";
    internal const string StoppingProtocolLine = "cohesion-resource: stopping";
    internal const string StoppedProtocolLine = "cohesion-resource: stopped";

    internal const int SuccessExitCode = 0;
    internal const int ConfigurationExitCode = 64;
    internal const int DependencyExitCode = 69;
    internal const int StartupExitCode = 70;
    internal const int RuntimeExitCode = 75;
    internal const int InterruptedExitCode = 130;
    internal const int TerminatedExitCode = 143;

    internal static async Task RunAsync<TContext>(
        Host<TContext> host,
        ResourceHostOptions options,
        CancellationToken cancellationToken = default)
        where TContext : HostContext
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(options);

        var runState = new RunState(host, options.ProtocolLineWriter);
        int exitCode;

        try
        {
            host.SetResourceShutdownTimeout(options.ShutdownTimeout);
            ApplyContentRoot(host.Context.Environment, options.ContentRootPath);

            using IDisposable signalSubscription = options.SignalSource.Subscribe(
                runState.RequestShutdown,
                options.StopEventName);

            await host.RunResourceAsync(runState, cancellationToken).ConfigureAwait(false);

            runState.ThrowIfProtocolLineFailed();
            exitCode = runState.IsDrainAborted
                ? GetDrainAbortExitCode(runState.StopSignal)
                : SuccessExitCode;
        }
        catch (Exception exception)
        {
            // ResourceHost is the executable boundary: every host failure is converted
            // to the frozen sysexits/v1 contract instead of escaping as a platform-
            // dependent unhandled-exception exit code.
            exitCode = ClassifyExitCode(
                exception,
                options,
                runState.HasReachedReady,
                runState.IsDrainAborted,
                runState.StopSignal);
        }

        options.ExitCodeHandler(exitCode);
    }

    internal static int ClassifyExitCode(
        Exception exception,
        ResourceHostOptions options,
        bool hasReachedReady,
        bool isDrainAborted,
        ResourceHostStopSignal stopSignal)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(options);

        ResourceHostFailureKind? failureKind = options.ClassifyException(exception);
        if (failureKind is ResourceHostFailureKind.Configuration)
        {
            return ConfigurationExitCode;
        }

        if (failureKind is ResourceHostFailureKind.Dependency)
        {
            return DependencyExitCode;
        }

        if (isDrainAborted)
        {
            return GetDrainAbortExitCode(stopSignal);
        }

        return hasReachedReady ? RuntimeExitCode : StartupExitCode;
    }

    private static void ApplyContentRoot(
        IHostEnvironment environment,
        System.IO.FileSystemPath contentRootPath)
    {
        if (environment is HostEnvironment hostEnvironment)
        {
            hostEnvironment.SetContentRootPath(contentRootPath);
            return;
        }

        if (!contentRootPath.Equals(environment.ContentRootPath))
        {
            throw new InvalidOperationException(
                "A resource host requires an environment whose content root can be applied.");
        }
    }

    private static int GetDrainAbortExitCode(ResourceHostStopSignal stopSignal)
    {
        return stopSignal is ResourceHostStopSignal.Interrupt
            ? InterruptedExitCode
            : TerminatedExitCode;
    }

    internal sealed class RunState
    {
        private readonly IHost _host;
        private readonly Action<string> _writeProtocolLine;
        private readonly Lock _protocolLock = new();
        private readonly TaskCompletionSource _stopCompletionSource =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Exception? _protocolLineException;
        private Exception? _stopException;
        private Action? _shutdownCallback;
        private int _hasReachedReady;
        private int _isStopping;
        private int _isStoppingLineWritten;
        private int _isStopped;
        private int _isDrainAborted;
        private int _stopSignal;

        internal RunState(IHost host, Action<string> writeProtocolLine)
        {
            _host = host;
            _writeProtocolLine = writeProtocolLine;
        }

        internal bool HasReachedReady => Volatile.Read(ref _hasReachedReady) != 0;

        internal bool HasBegunStopping => Volatile.Read(ref _isStopping) != 0;

        internal bool IsDrainAborted => Volatile.Read(ref _isDrainAborted) != 0;

        internal ResourceHostStopSignal StopSignal =>
            (ResourceHostStopSignal)Volatile.Read(ref _stopSignal);

        internal void HostStarted(Action? shutdownCallback)
        {
            Volatile.Write(ref _shutdownCallback, shutdownCallback);
        }

        internal void Started()
        {
            bool writeFailed;

            lock (_protocolLock)
            {
                if (_hasReachedReady != 0 || _isStopping != 0 || _isStopped != 0)
                {
                    return;
                }

                Volatile.Write(ref _hasReachedReady, 1);
                writeFailed = !TryWriteProtocolLine(ReadyProtocolLine);
            }

            if (writeFailed)
            {
                RequestShutdown(ResourceHostStopSignal.None);
            }
        }

        internal void Stopping()
        {
            lock (_protocolLock)
            {
                if (_isStoppingLineWritten != 0 || _isStopped != 0)
                {
                    return;
                }

                Volatile.Write(ref _isStopping, 1);
                _isStoppingLineWritten = 1;
                TryWriteProtocolLine(StoppingProtocolLine);
            }
        }

        internal void BeginStopping()
        {
            lock (_protocolLock)
            {
                if (_isStopped == 0)
                {
                    Volatile.Write(ref _isStopping, 1);
                }
            }
        }

        internal void Stopped()
        {
            lock (_protocolLock)
            {
                if (_isStopped != 0)
                {
                    return;
                }

                Volatile.Write(ref _isStopped, 1);
                TryWriteProtocolLine(StoppedProtocolLine);
            }
        }

        internal void DrainAborted()
        {
            Volatile.Write(ref _isDrainAborted, 1);
        }

        internal void CompleteStop(Exception? exception)
        {
            Volatile.Write(ref _stopException, exception);
            _stopCompletionSource.TrySetResult();
        }

        internal async Task WaitForStopAsync()
        {
            await _stopCompletionSource.Task.ConfigureAwait(false);

            Exception? exception = Volatile.Read(ref _stopException);
            if (exception is not null)
            {
                ExceptionDispatchInfo.Capture(exception).Throw();
            }
        }

        internal bool RequestShutdown(ResourceHostStopSignal stopSignal)
        {
            Action? shutdownCallback = Volatile.Read(ref _shutdownCallback);
            if (_host.Context is not HostContext context || shutdownCallback is null)
            {
                return false;
            }

            return context.TryShutdown(shutdownCallback, () =>
            {
                Interlocked.CompareExchange(
                    ref _stopSignal,
                    (int)stopSignal,
                    (int)ResourceHostStopSignal.None);
            });
        }

        internal void ThrowIfProtocolLineFailed()
        {
            Exception? exception = Volatile.Read(ref _protocolLineException);
            if (exception is not null)
            {
                ExceptionDispatchInfo.Capture(exception).Throw();
            }
        }

        private bool TryWriteProtocolLine(string protocolLine)
        {
            try
            {
                _writeProtocolLine(protocolLine);
                return true;
            }
            catch (Exception exception)
            {
                Interlocked.CompareExchange(ref _protocolLineException, exception, comparand: null);
                return false;
            }
        }
    }
}
