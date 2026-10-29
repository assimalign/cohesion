using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace Assimalign.Cohesion.Hosting.Internal;

/// <summary>
/// Turns the process's shutdown signals into a graceful shutdown of a host run.
/// </summary>
/// <remarks>
/// <para>
/// SIGTERM is how a platform such as Kubernetes or Docker stops a container, and SIGINT and SIGQUIT
/// are Ctrl+C and its relatives at a console. Left alone, each ends the process without stopping
/// the host, so in-flight work is cut off. A registration cancels the signal's default action and
/// requests the run's shutdown instead: the run drains, <c>RunAsync</c> returns, and the process
/// exits when its entry point does.
/// </para>
/// <para>
/// A run inside an <see cref="AppEnvironmentScope"/> shares its process with other programs, so the
/// process's signals are not its own: it stops on the scope's <see cref="AppEnvironmentScope.Shutdown"/>,
/// which the host of programs that entered the scope signals, and registers no process signal.
/// </para>
/// </remarks>
internal static class HostShutdownSignals
{
    private static readonly PosixSignal[] _signals = [PosixSignal.SIGINT, PosixSignal.SIGQUIT, PosixSignal.SIGTERM];

    /// <summary>
    /// Registers every shutdown signal the platform delivers, or inside a scope the scope's
    /// shutdown, until the returned registration is disposed.
    /// </summary>
    /// <param name="requestShutdown">Requests the run's shutdown; called once per signal received.</param>
    /// <returns>The registration, which removes the handlers when disposed.</returns>
    public static IDisposable Register(Action requestShutdown)
    {
        if (AppEnvironment.Current is { } scope)
        {
            return scope.Shutdown.Register(requestShutdown);
        }

        var registrations = new List<PosixSignalRegistration>(_signals.Length);
        foreach (PosixSignal signal in _signals)
        {
            try
            {
                registrations.Add(PosixSignalRegistration.Create(signal, context =>
                {
                    context.Cancel = true;
                    requestShutdown();
                }));
            }
            catch (PlatformNotSupportedException)
            {
                // The platform delivers no such signal, as in a browser.
            }
        }

        return new Registration(registrations);
    }

    /// <summary>
    /// Requests a run's shutdown by cancelling its token. Never throws: a signal handler that throws
    /// ends the process without draining it.
    /// </summary>
    /// <param name="shutdownSignal">The source of the run's token.</param>
    public static void RequestShutdown(CancellationTokenSource shutdownSignal)
    {
        try
        {
            shutdownSignal.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The run ended while the signal was being handled.
        }
        catch (AggregateException)
        {
            // A cancellation callback failed after every callback ran, so the run still received
            // the request.
        }
    }

    private sealed class Registration : IDisposable
    {
        private readonly List<PosixSignalRegistration> _registrations;

        public Registration(List<PosixSignalRegistration> registrations)
        {
            _registrations = registrations;
        }

        public void Dispose()
        {
            foreach (PosixSignalRegistration registration in _registrations)
            {
                registration.Dispose();
            }
        }
    }
}
