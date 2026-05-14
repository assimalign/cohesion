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
///   <item><description>Out-of-bailiwick NS resolution &#8211; when a referral carries no
///   in-bailiwick glue, the resolver recursively resolves the NS names through the same
///   cache and budget so production zones (.com, .net, etc.) work without manual glue
///   wiring.</description></item>
///   <item><description>Delegation caching &#8211; NS RRsets are cached by zone so
///   subsequent queries for names under a known zone skip the root&rarr;TLD walk.</description></item>
///   <item><description>QNAME minimization (RFC 9156) when
///   <see cref="IterativeDnsResolverOptions.EnableQNameMinimization"/> is set.</description></item>
///   <item><description>EDNS Cookies (RFC 7873) when
///   <see cref="IterativeDnsResolverOptions.EnableEdnsCookies"/> is set &#8211; server
///   cookies are cached by upstream IP and a BADCOOKIE response triggers one retry.</description></item>
///   <item><description>RFC 5966 TC&rarr;TCP fallback per step when
///   <see cref="IterativeDnsResolverOptions.TcpTransportFactory"/> is set.</description></item>
///   <item><description>Budget enforcement &#8211; bounded referral depth, bounded total
///   upstream exchanges per resolve, bounded NS-resolution recursion depth.</description></item>
/// </list>
/// </remarks>
public sealed class IterativeDnsResolver : DnsResolver
{
    private readonly IterativeDnsResolverOptions _options;
    private readonly DnsAnswerCache? _cache;
    private readonly DnsDelegationCache? _delegationCache;
    private readonly DnsCookieStore? _cookies;
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
        if (options.MaxNsResolutionDepth < 0)
        {
            throw new ArgumentException(
                $"{nameof(IterativeDnsResolverOptions)}.{nameof(IterativeDnsResolverOptions.MaxNsResolutionDepth)} must be non-negative.",
                nameof(options));
        }
        if (options.EdnsClientCookie is { } cc && cc.Length != 8)
        {
            throw new ArgumentException(
                $"{nameof(IterativeDnsResolverOptions)}.{nameof(IterativeDnsResolverOptions.EdnsClientCookie)} must be exactly 8 octets when set.",
                nameof(options));
        }

        _options = options;
        _cache = options.EnableCache
            ? new DnsAnswerCache(options.CacheCapacity, options.MinCacheTtl, options.MaxCacheTtl, options.TimeProvider)
            : null;
        _delegationCache = options.EnableDelegationCache
            ? new DnsDelegationCache(options.DelegationCacheCapacity, options.TimeProvider)
            : null;
        _cookies = options.EnableEdnsCookies
            ? options.EdnsClientCookie is { } seed
                ? new DnsCookieStore(seed)
                : DnsCookieStore.CreateRandom()
            : null;
        _rootEndpoints = new List<IPEndPoint>(options.RootEndpoints);
    }

    /// <summary>Number of entries in the answer cache, or zero when caching is disabled.</summary>
    public int CacheCount => _cache?.Count ?? 0;

    /// <summary>Number of entries in the delegation cache, or zero when disabled.</summary>
    public int DelegationCacheCount => _delegationCache?.Count ?? 0;

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

        var context = new ResolveContext(_options.MaxQueriesPerResolve);
        DnsMessage response = await ResolveCoreAsync(question, context, timeoutCts, cancellationToken).ConfigureAwait(false);
        _cache?.Put(question, response);
        return ThrowIfNonSuccess(response, question);
    }

    /// <inheritdoc />
    public override Task ClearCacheAsync(CancellationToken cancellationToken = default)
    {
        _cache?.Clear();
        _delegationCache?.Clear();
        return Task.CompletedTask;
    }

    private async Task<DnsMessage> ResolveCoreAsync(
        DnsQuestion question,
        ResolveContext context,
        CancellationTokenSource timeoutCts,
        CancellationToken externalToken)
    {
        // Cache short-circuit. This may be called from out-of-bailiwick NS resolution where
        // the parent question already passed cache check, but the NS-resolution sub-query
        // hits a fresh cache key.
        if (_cache is not null && _cache.TryGet(question, out DnsMessage? cached))
        {
            return cached;
        }

        // Cycle detection: if we're already resolving this question, fail rather than recurse
        // again. Prevents loops when one NS name's resolution requires another NS name whose
        // resolution requires the first.
        if (!context.MarkInFlight(question))
        {
            DnsException.ThrowTransport($"resolution cycle detected for {question}");
        }
        try
        {
            return await WalkAsync(question, context, timeoutCts, externalToken).ConfigureAwait(false);
        }
        finally
        {
            context.ClearInFlight(question);
        }
    }

    private async Task<DnsMessage> WalkAsync(
        DnsQuestion question,
        ResolveContext context,
        CancellationTokenSource timeoutCts,
        CancellationToken externalToken)
    {
        // Delegation-cache short-circuit: start at the most-specific known delegation rather
        // than the root.
        DnsName currentZone;
        IReadOnlyList<IPEndPoint> currentAuthorities;
        if (_delegationCache is not null
            && _delegationCache.TryGetClosest(question.Name, out DnsName? cachedZone, out IReadOnlyList<IPEndPoint>? cachedEndpoints))
        {
            currentZone = cachedZone.Value;
            currentAuthorities = cachedEndpoints;
        }
        else
        {
            currentZone = DnsName.Root;
            currentAuthorities = _rootEndpoints;
        }

        DnsException? lastFailure = null;

        for (int depth = 0; depth < _options.MaxReferralDepth; depth++)
        {
            DnsQuestion step = NextStepQuestion(question, currentZone);

            DnsMessage? response = null;
            foreach (IPEndPoint endpoint in currentAuthorities)
            {
                try
                {
                    response = await ExchangeStepAsync(endpoint, step, context, timeoutCts, externalToken).ConfigureAwait(false);
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

            DnsResponseCode rcode = DnsQueryHelper.EffectiveRcode(response!);
            bool isAuthoritative = (response!.Header.Flags & DnsHeaderFlags.AuthoritativeAnswer) != 0;

            if (rcode == DnsResponseCode.NXDomain && step.Name.Equals(question.Name))
            {
                return RebuildForOriginalQuestion(response, question);
            }
            if (rcode is not (DnsResponseCode.NoError or DnsResponseCode.NXDomain))
            {
                return RebuildForOriginalQuestion(response, question);
            }
            if (isAuthoritative && response.Answers.Count > 0)
            {
                if (step.Name.Equals(question.Name) && step.Type == question.Type)
                {
                    return RebuildForOriginalQuestion(response, question);
                }
                continue; // QNAME-min probe got an answer; re-issue the full question.
            }
            if (isAuthoritative && step.Name.Equals(question.Name) && step.Type == question.Type)
            {
                return RebuildForOriginalQuestion(response, question); // NODATA
            }

            var nsRecords = ExtractNsRecords(response.Authorities);
            if (nsRecords.Count == 0)
            {
                if (step.Name.Equals(question.Name))
                {
                    return RebuildForOriginalQuestion(response, question);
                }
                continue; // QNAME-min: advance one label.
            }

            DnsName? newZone = DnsBailiwick.ZoneOfNsRecords(nsRecords, question.Name);
            if (newZone is null || !DnsBailiwick.IsInBailiwick(newZone.Value, currentZone) || newZone.Value.Equals(currentZone))
            {
                DnsException.ThrowSpoofed(
                    $"referral from zone {currentZone} delegates to {newZone?.ToString() ?? "<unknown>"} which is not strictly below it");
            }

            // Collect in-bailiwick glue.
            List<IPEndPoint> newAuthorities = ResolveNsToEndpoints(nsRecords, response.Additionals, newZone!.Value);

            // Out-of-bailiwick or missing glue: recursively resolve NS names, charging the
            // shared budget. Skipped at depth limit to avoid runaway recursion.
            if (newAuthorities.Count == 0 && context.NsResolutionDepth < _options.MaxNsResolutionDepth)
            {
                newAuthorities = await ResolveOutOfBailiwickNsAsync(
                    nsRecords, question.Class, context, timeoutCts, externalToken).ConfigureAwait(false);
            }

            if (newAuthorities.Count == 0)
            {
                DnsException.ThrowTransport(
                    $"could not resolve any NS for delegation to {newZone}");
            }

            // Update delegation cache with the (zone, endpoints) we just learned.
            if (_delegationCache is not null)
            {
                TimeSpan nsTtl = ComputeNsTtl(nsRecords);
                _delegationCache.Put(newZone!.Value, newAuthorities, nsTtl);
            }

            currentZone = newZone!.Value;
            currentAuthorities = newAuthorities;
        }

        DnsException.ThrowTransport($"referral depth limit ({_options.MaxReferralDepth}) exceeded resolving {question}");
        throw new InvalidOperationException(); // unreachable
    }

    private async Task<List<IPEndPoint>> ResolveOutOfBailiwickNsAsync(
        IReadOnlyList<DnsRecord> nsRecords,
        DnsClass questionClass,
        ResolveContext context,
        CancellationTokenSource timeoutCts,
        CancellationToken externalToken)
    {
        var endpoints = new List<IPEndPoint>();
        context.EnterNsResolution();
        try
        {
            foreach (DnsRecord ns in nsRecords)
            {
                if (ns is not DnsNsRecord nsRr)
                {
                    continue;
                }
                // Try IPv4 first, fall back to IPv6.
                foreach (DnsRecordType type in new[] { DnsRecordType.A, DnsRecordType.AAAA })
                {
                    var subQuestion = new DnsQuestion(nsRr.NameServer, type, questionClass);
                    try
                    {
                        DnsMessage subResponse = await ResolveCoreAsync(
                            subQuestion, context, timeoutCts, externalToken).ConfigureAwait(false);
                        foreach (DnsRecord rr in subResponse.Answers)
                        {
                            if (rr is DnsARecord a)
                            {
                                endpoints.Add(new IPEndPoint(a.Address, 53));
                            }
                            else if (rr is DnsAaaaRecord aaaa)
                            {
                                endpoints.Add(new IPEndPoint(aaaa.Address, 53));
                            }
                        }
                    }
                    catch (DnsException)
                    {
                        // This NS name couldn't be resolved; try the next type or NS record.
                    }
                }

                if (endpoints.Count > 0)
                {
                    // We have at least one usable address — no need to resolve every NS.
                    // Stops doing extra work in the common case of well-mirrored NS names.
                    break;
                }
            }
        }
        finally
        {
            context.LeaveNsResolution();
        }
        return endpoints;
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
        return new DnsQuestion(minimized, DnsRecordType.NS, question.Class);
    }

    internal static DnsName MinimizeOneLabel(DnsName target, DnsName zone)
    {
        string[] tLabels = target.GetLabels();
        string[] zLabels = zone.GetLabels();

        if (tLabels.Length <= zLabels.Length)
        {
            return target;
        }
        int take = zLabels.Length + 1;
        int start = tLabels.Length - take;
        return new DnsName(string.Join('.', tLabels, start, take));
    }

    private async Task<DnsMessage> ExchangeStepAsync(
        IPEndPoint endpoint,
        DnsQuestion step,
        ResolveContext context,
        CancellationTokenSource timeoutCts,
        CancellationToken externalToken)
    {
        context.IncrementQueryCount();
        if (context.QueryCount > _options.MaxQueriesPerResolve)
        {
            DnsException.ThrowTransport(
                $"iterative query budget exhausted ({_options.MaxQueriesPerResolve})");
        }

        DnsMessage response = await ExchangeAtEndpointAsync(endpoint, step, timeoutCts, externalToken).ConfigureAwait(false);

        // BADCOOKIE: server demands a server cookie. If they sent one, we already cached
        // it via TryRecordServerCookie inside ExchangeAtEndpointAsync — retry once.
        // BADCOOKIE is an extended RCODE so we read the combined header + OPT value.
        if (DnsQueryHelper.EffectiveRcode(response) == DnsResponseCode.BadCookie && _cookies is not null)
        {
            context.IncrementQueryCount();
            response = await ExchangeAtEndpointAsync(endpoint, step, timeoutCts, externalToken).ConfigureAwait(false);
        }
        return response;
    }

    private async Task<DnsMessage> ExchangeAtEndpointAsync(
        IPEndPoint endpoint,
        DnsQuestion step,
        CancellationTokenSource timeoutCts,
        CancellationToken externalToken)
    {
        IReadOnlyList<DnsEdnsOption>? options = null;
        if (_cookies is not null && _options.EdnsPayloadSize > 0)
        {
            options = new DnsEdnsOption[] { _cookies.BuildOption(endpoint.Address) };
        }

        DnsMessage query = DnsQueryHelper.BuildQuery(
            DnsQueryHelper.NewTransactionId(),
            step,
            recursionDesired: false,
            ednsPayloadSize: _options.EdnsPayloadSize,
            ednsOptions: options);

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

        _cookies?.TryRecordServerCookie(endpoint.Address, response);
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

    private static TimeSpan ComputeNsTtl(IReadOnlyList<DnsRecord> nsRecords)
    {
        uint minTtl = uint.MaxValue;
        foreach (DnsRecord rr in nsRecords)
        {
            if (rr.TimeToLive < minTtl)
            {
                minTtl = rr.TimeToLive;
            }
        }
        return minTtl == uint.MaxValue ? TimeSpan.Zero : TimeSpan.FromSeconds(minTtl);
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

    /// <summary>
    /// Per-resolve state: shared budget, cycle-detection set, and NS-resolution depth tracker.
    /// One instance lives for the duration of <see cref="ResolveAsync"/>; recursive
    /// sub-resolves for out-of-bailiwick NS names reuse the same instance so the budget is
    /// charged across the whole resolve.
    /// </summary>
    private sealed class ResolveContext
    {
        private readonly HashSet<DnsQuestion> _inFlight = new();

        public ResolveContext(int maxQueries)
        {
            MaxQueries = maxQueries;
        }

        public int MaxQueries { get; }
        public int QueryCount { get; private set; }
        public int NsResolutionDepth { get; private set; }

        public void IncrementQueryCount() => QueryCount++;

        public bool MarkInFlight(DnsQuestion q) => _inFlight.Add(q);
        public void ClearInFlight(DnsQuestion q) => _inFlight.Remove(q);

        public void EnterNsResolution() => NsResolutionDepth++;
        public void LeaveNsResolution() => NsResolutionDepth--;
    }
}
