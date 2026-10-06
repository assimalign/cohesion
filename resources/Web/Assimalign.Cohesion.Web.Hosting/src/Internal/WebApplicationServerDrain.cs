using System;
using System.Threading;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Http.Connections;

namespace Assimalign.Cohesion.Web.Hosting.Internal;

/// <summary>
/// The two signals of the <see cref="WebApplicationServer"/> lame-duck drain, and the per-connection
/// registrations that carry them to the transport.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Begin"/> starts the drain. <see cref="Draining"/> stops the accept loop, and every live
/// connection begins its graceful close (<see cref="IHttpConnectionContext.BeginGracefulClose"/>): it
/// takes no new exchange and announces the close to its peer, while the exchanges it already yielded
/// run to completion. Nothing is cancelled.
/// </para>
/// <para>
/// <see cref="Abort"/> ends the drain when the stop's budget runs out. <see cref="Aborted"/> is the
/// token every connection is opened and received with and every exchange is executed and sent with,
/// so each exchange still running observes <see cref="IHttpContext.RequestCancelled"/>; and every
/// connection still open is aborted.
/// </para>
/// </remarks>
internal sealed class WebApplicationServerDrain : IDisposable
{
    private readonly CancellationTokenSource _draining = new();
    private readonly CancellationTokenSource _aborted = new();

    public WebApplicationServerDrain()
    {
        // Captured once: the server disposes the sources when it stops, a disposed source's Token
        // property throws, and a connection that outlives an expired budget still reads its tokens.
        Draining = _draining.Token;
        Aborted = _aborted.Token;
    }

    /// <summary>
    /// Gets the token cancelled when the drain begins: it stops the accept loop and a bind still in
    /// progress.
    /// </summary>
    public CancellationToken Draining { get; }

    /// <summary>
    /// Gets the token cancelled when the drain's budget runs out: the token connections are received
    /// with and exchanges are executed and sent with.
    /// </summary>
    public CancellationToken Aborted { get; }

    /// <summary>
    /// Begins the drain. Runs every registration's graceful close synchronously; a callback that
    /// throws is reported, after all of them ran, as an <see cref="AggregateException"/>.
    /// </summary>
    public void Begin()
    {
        _draining.Cancel();
    }

    /// <summary>
    /// Ends the drain by cancelling what is still in flight. Runs every registration's abort
    /// synchronously; a callback that throws is reported, after all of them ran, as an
    /// <see cref="AggregateException"/>.
    /// </summary>
    public void Abort()
    {
        _aborted.Cancel();
    }

    /// <summary>
    /// Ties one connection to the drain: its context begins a graceful close when the drain begins, and
    /// the connection is aborted when the drain is aborted. A signal already given applies at once.
    /// </summary>
    /// <param name="connection">The accepted connection.</param>
    /// <param name="context">The connection's opened context.</param>
    /// <returns>The registration; dispose it when the connection's service ends.</returns>
    public Registration Register(IHttpConnection connection, IHttpConnectionContext context)
    {
        return new Registration(
            Draining.UnsafeRegister(static state => ((IHttpConnectionContext)state!).BeginGracefulClose(), context),
            Aborted.UnsafeRegister(static state => ((IHttpConnection)state!).Abort(new ConnectionAbortedException(
                "The connection was aborted because the server's stop budget ran out before its exchanges finished.")),
                connection));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _draining.Dispose();
        _aborted.Dispose();
    }

    /// <summary>
    /// One connection's two drain registrations.
    /// </summary>
    internal readonly struct Registration : IDisposable
    {
        private readonly CancellationTokenRegistration _gracefulClose;
        private readonly CancellationTokenRegistration _abort;

        public Registration(CancellationTokenRegistration gracefulClose, CancellationTokenRegistration abort)
        {
            _gracefulClose = gracefulClose;
            _abort = abort;
        }

        /// <inheritdoc />
        public void Dispose()
        {
            _gracefulClose.Dispose();
            _abort.Dispose();
        }
    }
}
