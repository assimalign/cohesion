using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Assimalign.Cohesion.Dns.Internal;

namespace Assimalign.Cohesion.Dns;

/// <summary>
/// An iterative DNS resolver. Walks the delegation chain from the configured root hints,
/// following NS referrals zone by zone until an authoritative server answers the question.
/// </summary>
/// <remarks>
/// <para>
/// Implements the iterative algorithm from RFC 1034 &#167; 5.3.3 with modern hardening:
/// </para>
/// <list type="bullet">
///   <item><description>RFC 5452 transaction-id + question-echo validation on every
///   response.</description></item>
///   <item><description>Bailiwick check (RFC 8499 &#167; 6 / RFC 8806) on every referral &#8211;
///   NS records that delegate to a zone outside the queried authority's bailiwick are
///   discarded.</description></item>
///   <item><description>Glue policy &#8211; A/AAAA records in the additional section are only
///   trusted when their owner name is inside the delegated zone. Out-of-bailiwick glue is
///   discarded so a malicious authority cannot poison an unrelated zone.</description></item>
///   <item><description>QNAME minimization (RFC 9156) when
///   <see cref="IterativeDnsResolverOptions.EnableQNameMinimization"/> is set: each step probes
///   for NS with only the labels the current authority needs to know, hiding the rest of the
///   QNAME until we reach an authority closer to the leaf.</description></item>
///   <item><description>RFC 5966 TC&rarr;TCP fallback per step when
///   <see cref="IterativeDnsResolverOptions.TcpTransportFactory"/> is set.</description></item>
///   <item><description>Budget enforcement &#8211; bounded referral depth and bounded total
///   upstream exchanges per resolve to defeat pathological delegation chains and
///   denial-of-resolver loops.</description></item>
/// </list>
/// <para>
/// <strong>NS-name resolution.</strong> When a referral arrives without in-bailiwick glue,
/// the current implementation skips that NS rather than recursing to resolve the NS name.
/// In-bailiwick referrals from well-glued zones (the IANA root + every TLD that matters)
/// are unaffected; misconfigured zones with all-out-of-bailiwick NS names will surface
/// <see cref="DnsErrorCode.Transport"/>. Out-of-bailiwick NS resolution is a follow-up
/// concern (Story L01.01.08 PR6).
/// </para>
/// <para>
/// <strong>Caching.</strong> Successful, NXDOMAIN, and NODATA responses are cached by
/// question with TTL semantics matching <see cref="ForwardingDnsResolver"/>. Delegation
/// caching (caching NS RRsets at the zone level for subsequent walks) is a follow-up.
/// </para>
/// </remarks>
public sealed class IterativeDnsResolver : DnsResolver
{
    private readonly IterativeDnsResolverOptions _options;
    private readonly DnsAnswerCache? _cache;
    private readonly IReadOnlyList<IPEndPoint> _rootEndpoints;

    /// <summary>
    /// Initializes a new <see cref="IterativeDnsResolver"/>.
    /// </summary>
    public IterativeDnsResolver(IterativeDnsResolverOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.RootEndpoints.Count == 0)
        {
            throw new ArgumentException(
                $"{nameof(IterativeDnsResolverOptions)}.{nameof(IterativeDnsResolverOptions.RootEndpoints)} must contain at least one endpoint.",
                nameof(options));
        }
        if (options.UdpTransportFactory is null)
        {
            throw new ArgumentException(
                $"{nameof(IterativeDnsResolverOptions)}.{nameof(IterativeDnsResolverOptions.UdpTransportFactory)} is required.",
                nameof(options));
        }
        if (options.QueryTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentException(
                $"{nameof(IterativeDnsResolverOptions)}.{nameof(IterativeDnsResolverOptions.QueryTimeout)} must be positive.",
                nameof(options));
        }
        if (options.MaxReferralDepth <= 0)
        {
            throw new ArgumentException(
                $"{nameof(IterativeDnsResolverOptions)}.{nameof(IterativeDnsResolverOptions.MaxReferralDepth)} must be positive.",
                nameof(options));
        }
        if (options.MaxQueriesPerResolve <= 0)
        {
            throw new ArgumentException(
                $"{nameof(IterativeDnsResolverOptions)}.{nameof(IterativeDnsResolverOptions.MaxQueriesPerResolve)} must be positive.",
                nameof(options));
        }

        _options = options;
        _cache = options.EnableCache
            ? new DnsAnswerCache(options.CacheCapacity, options.MinCacheTtl, options.MaxCacheTtl, options.TimeProvider)
            : null;

        _rootEndpoints = new List<IPEndPoint>(options.RootEndpoints);
    }

    /// <summary>Number of entries currently held by the cache.</summary>
    public int CacheCount => _cache?.Count ?? 0;

    /// <inheritdoc />
    public override Task<DnsMessage> QueryAsync(DnsQuestion question, CancellationToken cancellationToken = default)
        => ResolveAsync(question, cancellationToken);

    /// <inheritdoc />
    public override async Task<DnsMessage> ResolveAsync(DnsQuestion question, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (question.Type == default)
        {
            throw new ArgumentException("Question type must be set.", nameof(question));
        }

        if (_cache is not null && _cache.TryGet(question, out DnsMessage? cached))
        {
            return ThrowIfNonSuccess(cached, question);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_options.QueryTimeout);

        DnsMessage response = await WalkAsync(question, timeoutCts, cancellationToken).ConfigureAwait(false);
        _cache?.Put(question, response);
        return ThrowIfNonSuccess(response, question);
    }

    /// <inheritdoc />
    public override Task ClearCacheAsync(CancellationToken cancellationToken = default)
    {
        _cache?.Clear();
        return Task.CompletedTask;
    }

    private async Task<DnsMessage> WalkAsync(
        DnsQuestion question,
        CancellationTokenSource timeoutCts,
        CancellationToken externalToken)
    {
        DnsName currentZone = DnsName.Root;
        IReadOnlyList<IPEndPoint> currentAuthorities = _rootEndpoints;

        int queries = 0;
        DnsException? lastFailure = null;

        for (int depth = 0; depth < _options.MaxReferralDepth; depth++)
        {
            // Decide what to send at this step. With QNAME minimization on we send NS for the
            // label one deeper than `currentZone`; otherwise we send the original question.
            DnsQuestion step = NextStepQuestion(question, currentZone);

            // Try each authority at this level until one answers or we exhaust the list.
            DnsMessage? response = null;
            foreach (IPEndPoint endpoint in currentAuthorities)
            {
                if (queries >= _options.MaxQueriesPerResolve)
                {
                    if (lastFailure is not null)
                    {
                        throw lastFailure;
                    }
                    DnsException.ThrowTransport(
                        $"iterative query budget exhausted ({_options.MaxQueriesPerResolve}) resolving {question}");
                }
                queries++;

                try
                {
                    response = await ExchangeStepAsync(endpoint, step, timeoutCts, externalToken).ConfigureAwait(false);
                    break;
                }
                catch (DnsException ex) when (ex.Code is DnsErrorCode.Transport or DnsErrorCode.Timeout or DnsErrorCode.Spoofed or DnsErrorCode.Malformed)
                {
                    lastFailure = ex;
                    continue;
                }
            }

            if (response is null)
            {
                if (lastFailure is not null)
                {
                    throw lastFailure;
                }
                DnsException.ThrowTransport($"no reachable authority for zone {currentZone}");
            }

            DnsResponseCode rcode = response!.Header.ResponseCode;
            bool isAuthoritative = (response.Header.Flags & DnsHeaderFlags.AuthoritativeAnswer) != 0;

            // NXDOMAIN against the full QNAME is a terminal answer.
            if (rcode == DnsResponseCode.NXDomain && step.Name.Equals(question.Name))
            {
                return RebuildForOriginalQuestion(response, question);
            }

            // SERVFAIL / REFUSED / etc against this authority — try a different zone? No,
            // we've already exhausted this level's authorities (we only got here with a
            // response). Propagate the failure.
            if (rcode is not (DnsResponseCode.NoError or DnsResponseCode.NXDomain))
            {
                return RebuildForOriginalQuestion(response, question);
            }

            // Authoritative answer for the step question.
            if (isAuthoritative && response.Answers.Count > 0)
            {
                // If we're asking the full question, this is the answer.
                if (step.Name.Equals(question.Name) && step.Type == question.Type)
                {
                    return RebuildForOriginalQuestion(response, question);
                }
                // QNAME-min probe got an authoritative answer at an intermediate label. That
                // means the current zone is also authoritative for the full QNAME. Re-issue
                // the original question against the same authority list.
                continue;
            }

            // Authoritative NoError with no answer at the full QNAME = NODATA.
            if (isAuthoritative && step.Name.Equals(question.Name) && step.Type == question.Type)
            {
                return RebuildForOriginalQuestion(response, question);
            }

            // Look for a referral in the authority section.
            var nsRecords = ExtractNsRecords(response.Authorities);
            if (nsRecords.Count == 0)
            {
                // No answer and no referral — this authority gave up. Try treating it as
                // NODATA at this label and advance.
                if (step.Name.Equals(question.Name))
                {
                    return RebuildForOriginalQuestion(response, question);
                }
                continue; // advance QNAME-min one label by re-evaluating NextStepQuestion
            }

            DnsName? newZone = DnsBailiwick.ZoneOfNsRecords(nsRecords, question.Name);
            if (newZone is null || !DnsBailiwick.IsInBailiwick(newZone.Value, currentZone) || newZone.Value.Equals(currentZone))
            {
                // Out-of-bailiwick or same-zone referral. Drop it — would loop or be spoofed.
                DnsException.ThrowSpoofed(
                    $"referral from zone {currentZone} delegates to {newZone?.ToString() ?? "<unknown>"} which is not strictly below it");
            }

            // Collect glue: A/AAAA records in additional whose owner names match the NS RDATA
            // AND are inside the new zone (in-bailiwick glue policy).
            var newAuthorities = ResolveNsToEndpoints(nsRecords, response.Additionals, newZone!.Value);
            if (newAuthorities.Count == 0)
            {
                // Referral with no usable glue. PR 5 scope: skip this level and surface a
                // transport error. PR 6 will add out-of-bailiwick NS resolution.
                DnsException.ThrowTransport(
                    $"referral to {newZone} has no in-bailiwick glue; NS-name resolution is not yet implemented");
            }

            currentZone = newZone!.Value;
            currentAuthorities = newAuthorities;
        }

        DnsException.ThrowTransport($"referral depth limit ({_options.MaxReferralDepth}) exceeded resolving {question}");
        throw new InvalidOperationException(); // unreachable
    }

    private DnsQuestion NextStepQuestion(DnsQuestion question, DnsName currentZone)
    {
        if (!_options.EnableQNameMinimization)
        {
            return question;
        }
        DnsName minimized = MinimizeOneLabel(question.Name, currentZone);
        if (minimized.Equals(question.Name))
        {
            return question;
        }
        // RFC 9156 §3.1 — use NS as the probe type for the minimized name.
        return new DnsQuestion(minimized, DnsRecordType.NS, question.Class);
    }

    /// <summary>
    /// Returns the name one label below <paramref name="zone"/> on the path to
    /// <paramref name="target"/>. Returns <paramref name="target"/> itself when the zone is
    /// already its parent.
    /// </summary>
    internal static DnsName MinimizeOneLabel(DnsName target, DnsName zone)
    {
        string[] tLabels = target.GetLabels();
        string[] zLabels = zone.GetLabels();

        if (tLabels.Length <= zLabels.Length)
        {
            return target;
        }
        // Take zLabels.Length + 1 labels from the end of target.
        int take = zLabels.Length + 1;
        int start = tLabels.Length - take;
        return new DnsName(string.Join('.', tLabels, start, take));
    }

    private async Task<DnsMessage> ExchangeStepAsync(
        IPEndPoint endpoint,
        DnsQuestion step,
        CancellationTokenSource timeoutCts,
        CancellationToken externalToken)
    {
        DnsMessage query = DnsQueryHelper.BuildQuery(
            DnsQueryHelper.NewTransactionId(),
            step,
            recursionDesired: false, // iterative — talking to authoritative servers
            ednsPayloadSize: _options.EdnsPayloadSize);

        DnsTransport udp = _options.UdpTransportFactory(endpoint);
        DnsMessage response;
        try
        {
            response = await DnsQueryHelper.ExchangeAsync(udp, query, timeoutCts, externalToken).ConfigureAwait(false);
        }
        finally
        {
            await udp.DisposeAsync().ConfigureAwait(false);
        }

        // RFC 5966 TC handling.
        if ((response.Header.Flags & DnsHeaderFlags.Truncated) != 0 && _options.TcpTransportFactory is not null)
        {
            DnsTransport tcp = _options.TcpTransportFactory(endpoint);
            try
            {
                response = await DnsQueryHelper.ExchangeAsync(tcp, query, timeoutCts, externalToken).ConfigureAwait(false);
            }
            finally
            {
                await tcp.DisposeAsync().ConfigureAwait(false);
            }
        }

        return response;
    }

    private static List<DnsRecord> ExtractNsRecords(IReadOnlyList<DnsRecord> authorities)
    {
        var ns = new List<DnsRecord>();
        foreach (DnsRecord rr in authorities)
        {
            if (rr.Type == DnsRecordType.NS)
            {
                ns.Add(rr);
            }
        }
        return ns;
    }

    private static List<IPEndPoint> ResolveNsToEndpoints(
        IReadOnlyList<DnsRecord> nsRecords,
        IReadOnlyList<DnsRecord> additionals,
        DnsName bailiwick)
    {
        var endpoints = new List<IPEndPoint>();
        foreach (DnsRecord ns in nsRecords)
        {
            if (ns is not DnsNsRecord nsRr)
            {
                continue;
            }
            foreach (DnsRecord add in additionals)
            {
                if (!add.Name.Equals(nsRr.NameServer))
                {
                    continue;
                }
                // In-bailiwick glue: glue records are only trusted if their owner is inside
                // the zone the NS delegates. Out-of-bailiwick glue is a poisoning vector.
                if (!DnsBailiwick.IsInBailiwick(add.Name, bailiwick))
                {
                    continue;
                }
                if (add is DnsARecord a)
                {
                    endpoints.Add(new IPEndPoint(a.Address, 53));
                }
                else if (add is DnsAaaaRecord aaaa && aaaa.Address.AddressFamily == AddressFamily.InterNetworkV6)
                {
                    endpoints.Add(new IPEndPoint(aaaa.Address, 53));
                }
            }
        }
        return endpoints;
    }

    private static DnsMessage RebuildForOriginalQuestion(DnsMessage response, DnsQuestion question)
    {
        // The response's question section may carry a minimized question (NS for an
        // intermediate label) rather than the original. Repackage it under the original
        // question so callers see the API-level question they asked about.
        if (response.Questions.Count == 1
            && response.Questions[0].Name.Equals(question.Name)
            && response.Questions[0].Type == question.Type
            && response.Questions[0].Class == question.Class)
        {
            return response;
        }

        return new DnsMessage(
            response.Header,
            new[] { question },
            response.Answers,
            response.Authorities,
            response.Additionals);
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
