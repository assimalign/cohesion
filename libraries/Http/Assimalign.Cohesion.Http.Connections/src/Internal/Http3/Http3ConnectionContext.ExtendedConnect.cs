using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http.Connections.Internal;

// The extended CONNECT tunnel (RFC 9220): finalizing the exchange once the application's handler
// returns. The accept itself is the exchange control's (Http3ExchangeControl.AcceptTunnelAsync). The
// tunnel's frames go straight to the request stream's output (see Http3ExtendedConnectStream); its
// reads drain the lazy request body, which carries a CONNECT's DATA outside the body-size cap and the
// Content-Length rule.
internal sealed partial class Http3ConnectionContext
{
    /// <summary>
    /// Finalizes an exchange whose tunnel was accepted, in place of writing a response: the tunnel sent
    /// the exchange's only head.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><description><b>The exchange was cancelled, or the head never reached the wire</b> — the
    /// request stream is reset with <c>H3_REQUEST_CANCELLED</c> (RFC 9114 §4.1.1), unless both sides had
    /// already ended it.</description></item>
    /// <item><description><b>Otherwise</b> — a peer still sending is told to stop with
    /// <c>STOP_SENDING(H3_NO_ERROR)</c> (RFC 9114 §4.1), as after any response that completes before its
    /// request, then the server's side is ended with a FIN if the application left it open, and reading
    /// stops. The stop goes first because on the QUIC driver the FIN releases the stream, which would stop
    /// the peer with the default code instead.</description></item>
    /// </list>
    /// The after-response interceptor hooks do not run: the tunnel took the exchange over.
    /// </remarks>
    /// <param name="context">The exchange being finalized.</param>
    /// <param name="tunnel">The exchange's accepted tunnel.</param>
    /// <param name="cancellationToken">A token that abandons waiting for the end of the tunnel to be written.</param>
    /// <returns>A task that completes once the exchange's request stream is finalized.</returns>
    private async ValueTask FinishTunnelAsync(Http3Context context, Http3ExtendedConnectStream tunnel, CancellationToken cancellationToken)
    {
        Http3RequestBodyStream requestBody = context.RequestBody;

        if (context.CancelRequested || !tunnel.IsHeadCommitted)
        {
            tunnel.Abandon();

            if (tunnel.IsWriteCompleted && requestBody.IsCompleted)
            {
                StopReadingRequestStream(requestBody, context.StreamId);
            }
            else
            {
                CancelRequestStream(context);
            }

            return;
        }

        requestBody.RefuseRemainder();
        await tunnel.CloseAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        StopReadingRequestStream(requestBody, context.StreamId);
    }
}
