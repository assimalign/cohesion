using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Assimalign.Cohesion.Dns.Internal;

namespace Assimalign.Cohesion.Dns;

/// <summary>
/// The simplest concrete <see cref="DnsClient"/>: one transport, one exchange per call, no
/// cache, no recursion, no forwarder rotation. Validates the response against the outgoing
/// query per RFC 5452 and surfaces non-success RCODEs as <see cref="DnsException"/>.
/// </summary>
/// <remarks>
/// <para>
/// Use <see cref="StubDnsClient"/> when you want a thin, predictable wrapper around a single
/// upstream &#8211; testing fixtures, low-level debugging, talking to a known authoritative
/// server. Most production callers want <see cref="ForwardingDnsResolver"/> or
/// <see cref="IterativeDnsResolver"/> instead.
/// </para>
/// <para>
/// When <see cref="StubDnsClientOptions.EnableEdnsCookies"/> is set, every outgoing query
/// carries an EDNS Cookie option (RFC 7873). The server cookie is cached for the lifetime of
/// the client (keyed by the transport's resolved IP) and a BADCOOKIE response (RCODE 23)
/// triggers one retry with the freshly-received cookie.
/// </para>
/// <para>
/// The client does not take ownership of the transport: the caller is responsible for
/// disposing the transport instance. Disposing the client itself is a no-op beyond marking
/// it as disposed.
/// </para>
/// </remarks>
public sealed class StubDnsClient : DnsClient
{
    private readonly StubDnsClientOptions _options;
    private readonly DnsCookieStore? _cookies;

    /// <summary>
    /// Initializes a new <see cref="StubDnsClient"/>.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="options"/>.<c>Transport</c> is
    /// <see langword="null"/> or the timeout is non-positive.</exception>
    public StubDnsClient(StubDnsClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Transport is null)
        {
            throw new ArgumentException(
                $"{nameof(StubDnsClientOptions)}.{nameof(StubDnsClientOptions.Transport)} is required.",
                nameof(options));
        }
        if (options.QueryTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentException(
                $"{nameof(StubDnsClientOptions)}.{nameof(StubDnsClientOptions.QueryTimeout)} must be positive.",
                nameof(options));
        }
        if (options.EdnsClientCookie is { } cc && cc.Length != 8)
        {
            throw new ArgumentException(
                $"{nameof(StubDnsClientOptions)}.{nameof(StubDnsClientOptions.EdnsClientCookie)} must be exactly 8 octets when set.",
                nameof(options));
        }

        _options = options;
        _cookies = options.EnableEdnsCookies
            ? options.EdnsClientCookie is { } seed
                ? new DnsCookieStore(seed)
                : DnsCookieStore.CreateRandom()
            : null;
    }

    /// <inheritdoc />
    public override async Task<DnsMessage> QueryAsync(DnsQuestion question, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (question.Type == default)
        {
            throw new ArgumentException("Question type must be set.", nameof(question));
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_options.QueryTimeout);

        DnsMessage response = await ExchangeAsync(question, timeoutCts, cancellationToken).ConfigureAwait(false);

        // BADCOOKIE retry: the cookie store has already cached the new server cookie via the
        // TryRecordServerCookie call inside ExchangeAsync. The RCODE is extended (>15) so we
        // must read the combined header + OPT value.
        if (DnsQueryHelper.EffectiveRcode(response) == DnsResponseCode.BadCookie && _cookies is not null)
        {
            response = await ExchangeAsync(question, timeoutCts, cancellationToken).ConfigureAwait(false);
        }

        return ThrowIfNonSuccess(response, question);
    }

    private async Task<DnsMessage> ExchangeAsync(
        DnsQuestion question,
        CancellationTokenSource timeoutCts,
        CancellationToken externalToken)
    {
        IPAddress? serverIp = TryGetServerIp(_options.Transport!.Endpoint);
        IReadOnlyList<DnsEdnsOption>? options = null;
        if (_cookies is not null && serverIp is not null && _options.EdnsPayloadSize > 0)
        {
            options = new DnsEdnsOption[] { _cookies.BuildOption(serverIp) };
        }

        DnsMessage query = DnsQueryHelper.BuildQuery(
            DnsQueryHelper.NewTransactionId(),
            question,
            recursionDesired: _options.RecursionDesired,
            ednsPayloadSize: _options.EdnsPayloadSize,
            ednsOptions: options);

        DnsMessage response = await DnsQueryHelper.ExchangeAsync(
            _options.Transport!,
            query,
            timeoutCts,
            externalToken).ConfigureAwait(false);

        if (_cookies is not null && serverIp is not null)
        {
            _cookies.TryRecordServerCookie(serverIp, response);
        }
        return response;
    }

    private static IPAddress? TryGetServerIp(EndPoint endpoint) => endpoint switch
    {
        IPEndPoint ip => ip.Address,
        _ => null,
    };

    private static DnsMessage ThrowIfNonSuccess(DnsMessage response, DnsQuestion question)
    {
        DnsResponseCode rcode = DnsQueryHelper.EffectiveRcode(response);
        if (rcode == DnsResponseCode.NoError)
        {
            return response;
        }
        if (rcode == DnsResponseCode.NXDomain)
        {
            DnsException.ThrowNotFound(question.ToString());
        }
        DnsException.ThrowServerFailure(question.ToString(), rcode);
        throw new InvalidOperationException(); // unreachable
    }
}
