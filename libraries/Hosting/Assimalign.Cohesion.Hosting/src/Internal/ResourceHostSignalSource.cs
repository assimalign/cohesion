using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace Assimalign.Cohesion.Hosting;

internal interface IResourceHostSignalSource
{
    IDisposable Subscribe(Func<ResourceHostStopSignal, bool> callback, string? stopEventName);
}

internal sealed class ResourceHostSignalSource : IResourceHostSignalSource
{
    private ResourceHostSignalSource()
    {
    }

    internal static ResourceHostSignalSource Instance { get; } = new();

    public IDisposable Subscribe(Func<ResourceHostStopSignal, bool> callback, string? stopEventName)
    {
        ArgumentNullException.ThrowIfNull(callback);

        var signalCallback = new SignalCallback(callback);
        EventWaitHandle? stopEvent = null;
        RegisteredWaitHandle? stopEventRegistration = null;
        IDisposable? processSignalSubscription = null;

        try
        {
            if (OperatingSystem.IsWindows() && !string.IsNullOrWhiteSpace(stopEventName))
            {
                stopEvent = EventWaitHandle.OpenExisting(stopEventName);
                stopEventRegistration = ThreadPool.RegisterWaitForSingleObject(
                    stopEvent,
                    static (state, _) =>
                    {
                        var registeredCallback = (SignalCallback)state!;
                        registeredCallback.Invoke(ResourceHostStopSignal.Terminate);
                    },
                    signalCallback,
                    Timeout.InfiniteTimeSpan,
                    executeOnlyOnce: true);
            }

            processSignalSubscription = ProcessSignalRouter.Subscribe(signalCallback.Invoke);

            return new Subscription(
                signalCallback,
                processSignalSubscription,
                stopEventRegistration,
                stopEvent);
        }
        catch
        {
            signalCallback.Deactivate();
            processSignalSubscription?.Dispose();
            stopEventRegistration?.Unregister(waitObject: null);
            stopEvent?.Dispose();
            throw;
        }
    }

    private sealed class SignalCallback
    {
        private Func<ResourceHostStopSignal, bool>? _callback;

        internal SignalCallback(Func<ResourceHostStopSignal, bool> callback)
        {
            _callback = callback;
        }

        internal bool Invoke(ResourceHostStopSignal stopSignal)
        {
            Func<ResourceHostStopSignal, bool>? callback = Volatile.Read(ref _callback);
            if (callback is null)
            {
                return false;
            }

            try
            {
                return callback(stopSignal);
            }
            catch
            {
                // ResourceHost records its signal before cancelling the run. If a user
                // cancellation callback throws, shutdown was still accepted and the
                // signal handler must remain non-throwing.
                return true;
            }
        }

        internal void Deactivate()
        {
            Interlocked.Exchange(ref _callback, null);
        }
    }

    private sealed class Subscription : IDisposable
    {
        private SignalCallback? _signalCallback;
        private IDisposable? _processSignalSubscription;
        private RegisteredWaitHandle? _stopEventRegistration;
        private EventWaitHandle? _stopEvent;

        internal Subscription(
            SignalCallback signalCallback,
            IDisposable processSignalSubscription,
            RegisteredWaitHandle? stopEventRegistration,
            EventWaitHandle? stopEvent)
        {
            _signalCallback = signalCallback;
            _processSignalSubscription = processSignalSubscription;
            _stopEventRegistration = stopEventRegistration;
            _stopEvent = stopEvent;
        }

        public void Dispose()
        {
            SignalCallback? signalCallback = Interlocked.Exchange(ref _signalCallback, null);
            IDisposable? processSignalSubscription = Interlocked.Exchange(
                ref _processSignalSubscription,
                null);
            RegisteredWaitHandle? stopEventRegistration = Interlocked.Exchange(
                ref _stopEventRegistration,
                null);
            EventWaitHandle? stopEvent = Interlocked.Exchange(ref _stopEvent, null);

            signalCallback?.Deactivate();
            processSignalSubscription?.Dispose();
            stopEventRegistration?.Unregister(waitObject: null);
            stopEvent?.Dispose();
        }
    }

    private static class ProcessSignalRouter
    {
        private static readonly Lock gate = new();
        private static readonly List<Func<ResourceHostStopSignal, bool>> subscribers = new();

        // Retaining these process-lifetime registrations makes one BCL handler fan out to
        // every active resource host instead of installing N handlers for N nested hosts.
        private static readonly PosixSignalRegistration[] registrations = CreateRegistrations();

        internal static IDisposable Subscribe(Func<ResourceHostStopSignal, bool> callback)
        {
            _ = registrations;

            lock (gate)
            {
                subscribers.Add(callback);
            }

            return new ProcessSignalSubscription(callback);
        }

        private static PosixSignalRegistration[] CreateRegistrations()
        {
            var created = new List<PosixSignalRegistration>(capacity: 4);

            if (OperatingSystem.IsWindows())
            {
                TryRegister(created, PosixSignal.SIGINT);
                TryRegister(created, PosixSignal.SIGTERM);
                TryRegister(created, PosixSignal.SIGHUP);
                TryRegister(created, PosixSignal.SIGQUIT);
            }
            else
            {
                TryRegister(created, PosixSignal.SIGINT);
                TryRegister(created, PosixSignal.SIGTERM);
                TryRegister(created, PosixSignal.SIGHUP);
            }

            return created.ToArray();
        }

        private static void TryRegister(List<PosixSignalRegistration> created, PosixSignal signal)
        {
            try
            {
                created.Add(PosixSignalRegistration.Create(signal, HandleSignal));
            }
            catch (PlatformNotSupportedException)
            {
                // Some Windows console signals have no BCL mapping. The supported
                // registrations and the named-event channel remain active.
            }
            catch (Win32Exception)
            {
                // A console-less Windows process cannot install console handlers. The
                // named event is the primary Cohesion stop channel on Windows.
            }
            catch (IOException)
            {
                // A process without a compatible console cannot install the handler.
                // Other supported registrations and the named-event channel remain active.
            }
        }

        private static void HandleSignal(PosixSignalContext context)
        {
            ResourceHostStopSignal stopSignal = context.Signal is PosixSignal.SIGINT or PosixSignal.SIGQUIT
                ? ResourceHostStopSignal.Interrupt
                : ResourceHostStopSignal.Terminate;
            Func<ResourceHostStopSignal, bool>[] callbacks;

            lock (gate)
            {
                callbacks = subscribers.ToArray();
            }

            bool accepted = false;
            foreach (Func<ResourceHostStopSignal, bool> callback in callbacks)
            {
                accepted |= callback(stopSignal);
            }

            context.Cancel = accepted;
        }

        private sealed class ProcessSignalSubscription : IDisposable
        {
            private Func<ResourceHostStopSignal, bool>? _callback;

            internal ProcessSignalSubscription(Func<ResourceHostStopSignal, bool> callback)
            {
                _callback = callback;
            }

            public void Dispose()
            {
                Func<ResourceHostStopSignal, bool>? callback = Interlocked.Exchange(ref _callback, null);
                if (callback is null)
                {
                    return;
                }

                lock (gate)
                {
                    subscribers.Remove(callback);
                }
            }
        }
    }
}
