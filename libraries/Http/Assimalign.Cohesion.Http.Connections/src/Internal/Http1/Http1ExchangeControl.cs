using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http.Connections.Internal;

/// <summary>
/// HTTP/1.1 <see cref="IHttpExchangeControl"/> — the per-exchange control surface offered to
/// response interceptors through <see cref="HttpExchangeInterceptorResponseContext.Control"/>. An HTTP/1.1
/// exchange owns its whole connection, so this is the one version whose control offers the full
/// surface: interim (<c>1xx</c>) writes straight onto the connection stream (RFC 9110 §15.2) and
/// the raw-stream takeover that protocol upgrades / <c>CONNECT</c> tunnels need (§7.8 / §9.3.6).
/// The extended CONNECT stream tunnel is HTTP/2 and HTTP/3 only, so it reports unsupported here.
/// Aborting is not a control mechanism — it is the application-owned
/// <see cref="IHttpContext.Cancel"/>, which <see cref="Http1ConnectionContext.SendAsync"/> honors
/// by writing no response and ending the connection after the exchange.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="TakeOver"/> flips the owning <see cref="Http1Context"/> into its finalized state
/// <em>before</em> surrendering the stream: <see cref="Http1Context.ResponseFinalized"/> makes
/// <see cref="Http1ConnectionContext.SendAsync"/> a no-op (nothing is double-written onto the raw
/// stream), and clearing <see cref="Http1Context.KeepAlive"/> ends the connection's request loop
/// after the current exchange (the parser never mistakes post-transition octets for a next
/// request). The handover is safe because the HTTP/1.1 parser reads byte-by-byte and never
/// buffers past the request it parsed — the surrendered stream starts exactly at the first
/// post-request octet.
/// </para>
/// <para>
/// The takeover is one-shot (a second <see cref="TakeOver"/> throws) so two features cannot both
/// claim the connection. The transport retains ownership of the underlying connection's disposal
/// — the caller owns I/O on the stream, not the socket lifetime.
/// </para>
/// </remarks>
internal sealed class Http1ExchangeControl : IHttpExchangeControl
{
    private readonly Http1Context _context;
    private readonly Stream _stream;
    private int _takenOver;

    public Http1ExchangeControl(Http1Context context, Stream stream)
    {
        _context = context;
        _stream = stream;
    }

    /// <inheritdoc />
    public bool HasResponseStarted =>
        _context.ResponseFinalized || _context.HasFinalResponseStarted;

    /// <inheritdoc />
    /// <remarks>
    /// The status <see cref="Http1Context.RequestBodyRejectedStatusCode"/> latched when a body read after
    /// dispatch failed on the client's side: <c>400</c> for malformed chunked framing or a malformed
    /// trailer section, <c>413</c> over the body-size cap, <c>408</c> below the minimum data rate, and
    /// <c>431</c> for a trailer section over the header-section bounds.
    /// <see cref="Http1ConnectionContext.SendAsync"/> answers the exchange with the same status.
    /// </remarks>
    public HttpStatusCode? ClientFaultStatusCode => _context.RequestBodyRejectedStatusCode;

    /// <inheritdoc />
    public bool CanWriteInterimResponse => !HasResponseStarted && !_context.CancelRequested;

    /// <inheritdoc />
    public async ValueTask WriteInterimResponseAsync(
        HttpStatusCode statusCode,
        IHttpHeaderCollection? headers = null,
        CancellationToken cancellationToken = default)
    {
        HttpInterimResponseRules.ValidateInterimStatusCode(statusCode);

        if (!CanWriteInterimResponse)
        {
            throw new InvalidOperationException(
                "An interim response cannot be sent after the final HTTP/1.1 response has started.");
        }

        await Http1MessageWriter.WriteInterimResponseAsync(_stream, statusCode, headers, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public bool CanTakeOver =>
        _takenOver == 0 && !HasResponseStarted && !_context.CancelRequested;

    /// <inheritdoc />
    public Stream TakeOver()
    {
        // Validate BEFORE latching the one-shot claim: a caller whose takeover is impossible must
        // not permanently consume the capability (a later legitimate caller would then get a
        // misleading "already taken over" error).
        if (HasResponseStarted || _context.CancelRequested)
        {
            throw new InvalidOperationException(
                "The exchange can no longer be taken over: the final response has started or the exchange was aborted.");
        }

        if (Interlocked.Exchange(ref _takenOver, 1) == 1)
        {
            throw new InvalidOperationException(
                "The connection has already been taken over for this exchange.");
        }

        _context.ResponseFinalized = true;
        _context.KeepAlive = false;
        return _stream;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Always <see langword="false"/>: HTTP/1.1 has no extended CONNECT. Its <c>CONNECT</c> tunnels and
    /// protocol upgrades take the whole connection over (<see cref="TakeOver"/>).
    /// </remarks>
    public bool CanAcceptTunnel => false;

    /// <inheritdoc />
    public ValueTask<Stream> AcceptTunnelAsync(CancellationToken cancellationToken = default)
    {
        return ValueTask.FromException<Stream>(new InvalidOperationException(
            "HTTP/1.1 has no extended CONNECT stream tunnel; a CONNECT or a protocol upgrade takes the connection over instead (RFC 9110 §9.3.6, §7.8)."));
    }
}
