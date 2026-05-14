using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Assimalign.Cohesion.Dns.Internal;

namespace Assimalign.Cohesion.Dns;

/// <summary>
/// A forwarding DNS resolver. Sends every question to one of the configured upstream
/// forwarders, validates the response against the outgoing query (RFC 5452 transaction-id +
/// question-section echo), and caches positive / NXDOMAIN / NODATA answers by TTL.
/// </summary>
/// <remarks>
/// <para>
/// "Forwarding" here means the resolver does not perform its own iterative walk &#8211; it
/// delegates the recursion to the configured upstream(s) and inherits their recursion-cache
/// behavior. This is the right shape for stub resolvers, side-car caches, and any deployment
/// where a trusted upstream (a corporate resolver, a public service like
/// <c>1.1.1.1</c>/<c>9.9.9.9</c>, a local <c>unbound</c>) already exists.
/// </para>
/// <para>
/// Iterative resolution from root hints &#8211; with referral chasing, bailiwick checks, glue
/// policing, and QNAME minimization &#8211; is a separate concern that lands in a follow-up
/// (Stories L01.01.08.06.02 / 06.03).
/// </para>
/// <para>
/// <strong>Spoofing protection.</strong> Every outgoing query gets a fresh
/// cryptographically-random 16-bit transaction id
/// (<see cref="RandomNumberGenerator.GetInt32(int)"/>). The response is rejected with
/// <see cref="DnsErrorCode.Spoofed"/> if the id, question count, or question triple
/// (name + type + class) does not echo the query. Source-port randomization is handled by
/// the OS (the underlying socket is created with an ephemeral local port).
/// </para>
/// <para>
/// <strong>TC fallback.</strong> When a UDP response carries the TC flag (RFC 5966), the
/// resolver retries the same question on the TCP transport configured in
/// <see cref="ForwardingDnsResolverOptions.TcpFallbacks"/>. If no TCP fallback is registered
/// for that UDP forwarder, the truncated response is surfaced to the caller as-is &#8211;
/// they can decide whether to retry, ignore the TC bit, or treat the partial answer as
/// authoritative for their purposes.
/// </para>
/// <para>
/// <strong>Forwarder rotation.</strong> Forwarders are tried in the order configured. A
/// failure that surfaces as <see cref="DnsErrorCode.Transport"/> or
/// <see cref="DnsErrorCode.Timeout"/> fails over to the next forwarder; a successful exchange
/// that returns NXDOMAIN, NODATA, or another RCODE is the authoritative answer and is not
/// retried elsewhere.
/// </para>
/// </remarks>
public sealed class ForwardingDnsResolver : DnsResolver
{
    private readonly ForwardingDnsResolverOptions _options;
    private readonly DnsAnswerCache? _cache;

    /// <summary>
    /// Initializes a new <see cref="ForwardingDnsResolver"/>.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="options"/>.<c>Forwarders</c> is empty
    /// or contains <see langword="null"/>, or a timeout / cache parameter is invalid.</exception>
    public ForwardingDnsResolver(ForwardingDnsResolverOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Forwarders.Count == 0)
        {
            throw new ArgumentException(
                $"{nameof(ForwardingDnsResolverOptions)}.{nameof(ForwardingDnsResolverOptions.Forwarders)} must contain at least one transport.",
                nameof(options));
        }
        foreach (DnsTransport t in options.Forwarders)
        {
            if (t is null)
            {
                throw new ArgumentException(
                    "Forwarder list contains a null entry.",
                    nameof(options));
            }
        }
        if (options.QueryTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentException(
                $"{nameof(ForwardingDnsResolverOptions)}.{nameof(ForwardingDnsResolverOptions.QueryTimeout)} must be positive.",
                nameof(options));
        }

        _options = options;
        _cache = options.EnableCache
            ? new DnsAnswerCache(options.CacheCapacity, options.MinCacheTtl, options.MaxCacheTtl, options.TimeProvider)
            : null;
    }

    /// <summary>
    /// Number of entries currently held by the cache, or zero when caching is disabled.
    /// Exposed for diagnostics and tests; not part of the long-term public surface guarantee.
    /// </summary>
    public int CacheCount => _cache?.Count ?? 0;

    /// <inheritdoc />
    public override Task<DnsMessage> QueryAsync(DnsQuestion question, CancellationToken cancellationToken = default)
        => ResolveAsync(question, cancellationToken);

    /// <inheritdoc />
    /// <remarks>
    /// In forwarding mode the answer comes from a recursive upstream, so
    /// <see cref="ResolveAsync"/> and <see cref="QueryAsync"/> are the same operation.
    /// </remarks>
    public override async Task<DnsMessage> ResolveAsync(DnsQuestion question, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (question.Type == default)
        {
            throw new ArgumentException("Question type must be set.", nameof(question));
        }

        // Cache check first. We invoke ThrowIfNonSuccess on the cached message so a cached
        // NXDOMAIN / SERVFAIL raises the same exception shape as a fresh query.
        if (_cache is not null && _cache.TryGet(question, out DnsMessage? cached))
        {
            return ThrowIfNonSuccess(cached, question);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_options.QueryTimeout);

        DnsException? lastFailure = null;
        foreach (DnsTransport forwarder in _options.Forwarders)
        {
            DnsMessage response;
            try
            {
                response = await ExchangeAsync(forwarder, question, timeoutCts, cancellationToken).ConfigureAwait(false);
            }
            catch (DnsException ex) when (ex.Code is DnsErrorCode.Transport or DnsErrorCode.Timeout)
            {
                // Failover to the next forwarder. Remember the last error so we can throw it
                // if every forwarder fails.
                lastFailure = ex;
                continue;
            }

            // TC fallback: if the answer is truncated and we have a TCP transport for this
            // forwarder, retry the exchange over TCP.
            if ((response.Header.Flags & DnsHeaderFlags.Truncated) != 0
                && _options.TcpFallbacks.TryGetValue(forwarder, out DnsTransport? tcp))
            {
                try
                {
                    response = await ExchangeAsync(tcp, question, timeoutCts, cancellationToken).ConfigureAwait(false);
                }
                catch (DnsException ex) when (ex.Code is DnsErrorCode.Transport or DnsErrorCode.Timeout)
                {
                    // The TCP fallback failed; keep the truncated UDP answer and let the
                    // caller decide. Don't fail over to the next forwarder because we already
                    // have a (truncated) authoritative-from-upstream answer.
                    _ = ex;
                }
            }

            _cache?.Put(question, response);
            return ThrowIfNonSuccess(response, question);
        }

        // Every forwarder failed.
        if (lastFailure is not null)
        {
            throw lastFailure;
        }
        // Defensive — empty forwarder list is rejected in the constructor.
        DnsException.ThrowTransport("no forwarders attempted");
        throw new InvalidOperationException();
    }

    /// <inheritdoc />
    public override Task ClearCacheAsync(CancellationToken cancellationToken = default)
    {
        _cache?.Clear();
        return Task.CompletedTask;
    }

    private async Task<DnsMessage> ExchangeAsync(
        DnsTransport transport,
        DnsQuestion question,
        CancellationTokenSource timeoutCts,
        CancellationToken externalToken)
    {
        ushort id = (ushort)RandomNumberGenerator.GetInt32(0, 65_536);
        DnsMessage query = BuildQuery(id, question);
        byte[] requestBytes = SerializeQuery(query);

        ReadOnlyMemory<byte> responseBytes;
        try
        {
            responseBytes = await transport
                .ExchangeAsync(requestBytes, timeoutCts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !externalToken.IsCancellationRequested)
        {
            DnsException.ThrowTimeout($"{question.Name} {question.Type}");
            throw; // unreachable
        }

        DnsMessage response;
        try
        {
            response = DnsMessage.Parse(responseBytes.Span);
        }
        catch (DnsException)
        {
            throw;
        }
        catch (Exception ex)
        {
            DnsException.ThrowMalformed($"failed to parse response for {question.Name} {question.Type}", ex);
            throw; // unreachable
        }

        ValidateResponse(response, query);
        return response;
    }

    private DnsMessage BuildQuery(ushort id, DnsQuestion question)
    {
        bool useEdns = _options.EdnsPayloadSize > 0;
        var header = new DnsHeader(
            id,
            DnsHeaderFlags.RecursionDesired,
            DnsOpCode.Query,
            DnsResponseCode.NoError,
            questionCount: 1,
            answerCount: 0,
            authorityCount: 0,
            additionalCount: useEdns ? (ushort)1 : (ushort)0);

        IReadOnlyList<DnsRecord> additionals = useEdns
            ? new DnsRecord[] { new DnsOptRecord(_options.EdnsPayloadSize) }
            : Array.Empty<DnsRecord>();

        return new DnsMessage(
            header,
            new[] { question },
            Array.Empty<DnsRecord>(),
            Array.Empty<DnsRecord>(),
            additionals);
    }

    private static byte[] SerializeQuery(DnsMessage query)
    {
        // 1232 octets is the modern guidance for UDP-safe DNS messages (RFC 6891 §6.2.4 +
        // dnsflagday.net). Queries are always small relative to that bound.
        byte[] buffer = new byte[1232];
        int written = query.WriteTo(buffer);
        // Trim to actual length; the transport API accepts a ReadOnlyMemory of any size.
        byte[] trimmed = new byte[written];
        Buffer.BlockCopy(buffer, 0, trimmed, 0, written);
        return trimmed;
    }

    private static void ValidateResponse(DnsMessage response, DnsMessage query)
    {
        if (response.Header.Id != query.Header.Id)
        {
            DnsException.ThrowSpoofed(
                $"response transaction id 0x{response.Header.Id:X4} does not match query id 0x{query.Header.Id:X4}");
        }
        if ((response.Header.Flags & DnsHeaderFlags.Response) == 0)
        {
            DnsException.ThrowSpoofed("response message does not have the QR flag set");
        }
        if (response.Questions.Count != query.Questions.Count)
        {
            DnsException.ThrowSpoofed(
                $"response question count {response.Questions.Count} does not echo query count {query.Questions.Count}");
        }
        for (int i = 0; i < query.Questions.Count; i++)
        {
            DnsQuestion rq = response.Questions[i];
            DnsQuestion qq = query.Questions[i];
            if (!rq.Name.Equals(qq.Name) || rq.Type != qq.Type || rq.Class != qq.Class)
            {
                DnsException.ThrowSpoofed(
                    $"response question {i} ({rq}) does not echo query question ({qq})");
            }
        }
    }

    private static DnsMessage ThrowIfNonSuccess(DnsMessage response, DnsQuestion question)
    {
        DnsResponseCode rcode = response.Header.ResponseCode;
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
