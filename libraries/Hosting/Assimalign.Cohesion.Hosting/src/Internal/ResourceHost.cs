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

    internal static void AssertContentRoot(
        IHostEnvironment environment,
        System.IO.FileSystemPath contentRootPath)
    {
        if (!contentRootPath.Equals(environment.ContentRootPath))
        {
            throw new InvalidOperationException(
                "The host content root must match the ambient resource content root.");
        }
    }

    internal static int GetDrainAbortExitCode(ResourceHostStopSignal stopSignal)
    {
        return stopSignal is ResourceHostStopSignal.Interrupt
            ? InterruptedExitCode
            : TerminatedExitCode;
    }

    internal sealed class RunState : IHostRunObserver
    {
        private readonly IHostRun _run;
        private readonly Action<string>? _writeProtocolLine;
        private readonly Lock _protocolLock = new();
        private Exception? _protocolLineException;
        private int _hasReachedReady;
        private int _isStopping;
        private int _isStoppingLineWritten;
        private int _isStopped;
        private int _isDrainAborted;
        private int _stopSignal;

        internal RunState(IHostRun run, Action<string>? writeProtocolLine)
        {
            _run = run;
            _writeProtocolLine = writeProtocolLine;
        }

        internal bool HasReachedReady => Volatile.Read(ref _hasReachedReady) != 0;

        internal bool IsDrainAborted => Volatile.Read(ref _isDrainAborted) != 0;

        internal ResourceHostStopSignal StopSignal =>
            (ResourceHostStopSignal)Volatile.Read(ref _stopSignal);

        public void Started(IHost host)
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

        public void Stopping(IHost host)
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

        public void Stopped(IHost host)
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

        public void DrainAborted(IHost host)
        {
            Volatile.Write(ref _isDrainAborted, 1);
        }

        internal bool RequestShutdown(ResourceHostStopSignal stopSignal)
        {
            return _run.TryShutdown(() =>
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
            if (_writeProtocolLine is null)
            {
                return true;
            }

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
