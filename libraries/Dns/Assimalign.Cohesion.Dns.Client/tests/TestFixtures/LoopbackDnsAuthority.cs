using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Threading;

namespace Assimalign.Cohesion.Dns.Tests;

/// <summary>
/// A loopback DNS server that pretends to be authoritative for one zone. Designed to be
/// composed with sibling authorities to model the full root &rarr; TLD &rarr; SLD chain in
/// tests for <see cref="IterativeDnsResolver"/>.
/// </summary>
/// <remarks>
/// <para>
/// The authority knows two kinds of data:
/// </para>
/// <list type="bullet">
///   <item><description><strong>Delegations</strong>: zones it has been told to delegate to
///   other authorities. Queries inside a delegated zone produce a referral (NS records in
///   the authority section + matching A glue in the additional section).</description></item>
///   <item><description><strong>Records</strong>: A / AAAA / NS / CNAME etc. it serves
///   authoritatively for its own zone.</description></item>
/// </list>
/// <para>
/// Behavior:
/// </para>
/// <list type="bullet">
///   <item><description>Question inside a delegated child zone &rarr; referral (NoError,
///   AA=0, NS + glue).</description></item>
///   <item><description>Question for a name in this zone with matching record &rarr;
///   authoritative answer (NoError, AA=1, answer set).</description></item>
///   <item><description>Question for a name in this zone with no record at all &rarr;
///   NXDOMAIN with SOA.</description></item>
///   <item><description>Question for a name in this zone with no record of the queried type
///   &rarr; NODATA (NoError, AA=1, empty answer, SOA in authority).</description></item>
///   <item><description>Question completely outside this zone &rarr; REFUSED.</description></item>
/// </list>
/// </remarks>
internal sealed class LoopbackDnsAuthority : IAsyncDisposable
{
    // Registry mapping virtual IPs (127.42.x.y) to real loopback endpoints. Glue records
    // refer to the virtual IPs; the resolver's transport factory translates them to the
    // ephemeral ports the loopback sockets actually listen on.
    private static readonly ConcurrentDictionary<IPAddress, IPEndPoint> _virtualMap = new();
    private static int _nextVirtualIp;

    private readonly LoopbackUdpDnsServer _server;
    private readonly DnsName _zone;
    private readonly DnsSoaRecord _soa;
    private readonly List<DnsRecord> _records = new();
    private readonly Dictionary<DnsName, Delegation> _delegations = new();

    public LoopbackDnsAuthority(DnsName zone, DnsSoaRecord? soa = null)
    {
        _zone = zone;
        // For the root zone the per-label prefix yields names like "ns1.." which look valid
        // to DnsName.Validate but are semantically broken on the wire. Build a flat name in
        // that case instead.
        DnsName ns1 = zone.IsRoot ? new DnsName("ns1.test-root") : new DnsName($"ns1.{zone.Value.TrimEnd('.')}");
        DnsName hostmaster = zone.IsRoot ? new DnsName("hostmaster.test-root") : new DnsName($"hostmaster.{zone.Value.TrimEnd('.')}");
        _soa = soa ?? new DnsSoaRecord(
            name: zone,
            primaryNameServer: ns1,
            responsibleMailbox: hostmaster,
            serial: 1,
            refreshInterval: 7200,
            retryInterval: 3600,
            expireLimit: 1_209_600,
            minimumTtl: 60,
            timeToLive: 300);

        _server = new LoopbackUdpDnsServer();
        _server.OnRequest(Handle);

        // Allocate a unique virtual address inside 127.42/16 and register the mapping so the
        // resolver's transport factory (see CreateTransportFactory below) can translate the
        // glue-record IP back to the ephemeral port we're actually listening on.
        int idx = Interlocked.Increment(ref _nextVirtualIp);
        VirtualAddress = new IPAddress(new byte[] { 127, 42, (byte)(idx >> 8), (byte)(idx & 0xFF) });
        VirtualEndPoint = new IPEndPoint(VirtualAddress, 53);
        _virtualMap[VirtualAddress] = _server.EndPoint;
    }

    /// <summary>The real loopback endpoint this authority listens on.</summary>
    public IPEndPoint EndPoint => _server.EndPoint;

    /// <summary>
    /// A unique-to-this-process IP address (127.42.x.y) that appears in glue records the
    /// authority produces. The transport factory returned by <see cref="CreateTransportFactory"/>
    /// translates this address back to <see cref="EndPoint"/> for outgoing exchanges.
    /// </summary>
    public IPAddress VirtualAddress { get; }

    /// <summary>The virtual endpoint (VirtualAddress, port 53) the resolver sees.</summary>
    public IPEndPoint VirtualEndPoint { get; }

    public DnsName Zone => _zone;

    /// <summary>Total inbound queries served. Useful for verifying QNAME-min behavior.</summary>
    public int RequestCount { get; private set; }

    /// <summary>The most recent question received. Useful for verifying QNAME-min behavior.</summary>
    public DnsQuestion? LastQuestion { get; private set; }

    /// <summary>
    /// Optional hook invoked once per inbound query, before the authority responds. Tests
    /// use this to capture EDNS options, header flags, or anything else not covered by the
    /// <see cref="LastQuestion"/> shortcut.
    /// </summary>
    public Action<DnsMessage>? OnInboundQuery { get; set; }

    /// <summary>
    /// Adds a record to this authority's zone.
    /// </summary>
    public LoopbackDnsAuthority AddRecord(DnsRecord record)
    {
        _records.Add(record);
        return this;
    }

    /// <summary>
    /// Adds a delegation to another authority. The current authority will respond to queries
    /// inside <paramref name="childZone"/> with NS records pointing at
    /// <paramref name="nsName"/> and glue containing <paramref name="child"/>'s loopback
    /// endpoint.
    /// </summary>
    public LoopbackDnsAuthority Delegate(
        DnsName childZone,
        DnsName nsName,
        LoopbackDnsAuthority child,
        bool includeGlue = true)
    {
        // Use the child's virtual endpoint in the glue. The resolver translates this back
        // to the actual ephemeral port via the factory returned by CreateTransportFactory.
        _delegations[childZone] = new Delegation(childZone, nsName, child.VirtualEndPoint, includeGlue);
        return this;
    }

    /// <summary>
    /// Adds a delegation with explicit glue addresses (used for tests that want to inject
    /// out-of-bailiwick glue to verify the resolver rejects it).
    /// </summary>
    public LoopbackDnsAuthority DelegateRaw(
        DnsName childZone,
        DnsName nsName,
        IPEndPoint glueEndpoint,
        DnsName glueOwner)
    {
        _delegations[childZone] = new Delegation(childZone, nsName, glueEndpoint, includeGlue: true)
        {
            GlueOwnerOverride = glueOwner,
        };
        return this;
    }

    private byte[] Handle(byte[] request)
    {
        RequestCount++;
        DnsMessage query;
        try
        {
            query = DnsMessage.Parse(request);
        }
        catch
        {
            return Array.Empty<byte>();
        }
        if (query.Questions.Count == 0)
        {
            return Array.Empty<byte>();
        }

        DnsQuestion q = query.Questions[0];
        LastQuestion = q;
        OnInboundQuery?.Invoke(query);

        // 1. Refused if entirely outside our zone.
        if (!IsInZoneOrChild(q.Name))
        {
            return DnsTestMessages.BuildWithRcode(query, DnsResponseCode.Refused);
        }

        // 2. Referral if inside a delegated child zone.
        Delegation? matchedDelegation = FindMatchingDelegation(q.Name);
        if (matchedDelegation is not null && !q.Name.Equals(_zone))
        {
            return BuildReferral(query, matchedDelegation);
        }

        // 3. Look for matching records of the queried type and exact owner.
        var matchedAnswers = new List<DnsRecord>();
        bool anyForName = false;
        foreach (DnsRecord rr in _records)
        {
            if (!rr.Name.Equals(q.Name))
            {
                continue;
            }
            anyForName = true;
            if (rr.Type == q.Type)
            {
                matchedAnswers.Add(rr);
            }
        }

        if (matchedAnswers.Count > 0)
        {
            return DnsTestMessages.BuildAnswer(query, matchedAnswers, authoritative: true);
        }

        // 4. NoData when the name exists but the type doesn't.
        if (anyForName)
        {
            return BuildNoData(query);
        }

        // 5. NS query for our own zone — produce an authoritative answer (used by QNAME-min).
        if (q.Type == DnsRecordType.NS && q.Name.Equals(_zone))
        {
            var nsRecords = new List<DnsRecord>();
            foreach (DnsRecord rr in _records)
            {
                if (rr.Name.Equals(_zone) && rr.Type == DnsRecordType.NS)
                {
                    nsRecords.Add(rr);
                }
            }
            if (nsRecords.Count > 0)
            {
                return DnsTestMessages.BuildAnswer(query, nsRecords, authoritative: true);
            }
            // No explicit NS record — return NoData. Tests can populate NS if they care.
            return BuildNoData(query);
        }

        // 6. NXDOMAIN — name doesn't exist in this zone and isn't delegated.
        return DnsTestMessages.BuildNxDomain(query, _soa);
    }

    private byte[] BuildReferral(DnsMessage query, Delegation delegation)
    {
        var ns = new DnsNsRecord(delegation.Zone, delegation.NsName, timeToLive: 300);
        var authority = new DnsRecord[] { ns };

        IReadOnlyList<DnsRecord> additionals = Array.Empty<DnsRecord>();
        if (delegation.IncludeGlue)
        {
            DnsName glueOwner = delegation.GlueOwnerOverride ?? delegation.NsName;
            additionals = new DnsRecord[]
            {
                new DnsARecord(glueOwner, delegation.GlueEndpoint.Address, timeToLive: 300),
            };
        }

        // Non-authoritative referral (AA = 0).
        var header = new DnsHeader(
            query.Header.Id,
            DnsHeaderFlags.Response,
            DnsOpCode.Query,
            DnsResponseCode.NoError,
            (ushort)query.Questions.Count,
            0,
            (ushort)authority.Length,
            (ushort)additionals.Count);

        var response = new DnsMessage(
            header,
            query.Questions,
            Array.Empty<DnsRecord>(),
            authority,
            additionals);

        byte[] buffer = new byte[4096];
        int written = response.WriteTo(buffer);
        byte[] trimmed = new byte[written];
        Buffer.BlockCopy(buffer, 0, trimmed, 0, written);
        return trimmed;
    }

    private byte[] BuildNoData(DnsMessage query)
    {
        var header = new DnsHeader(
            query.Header.Id,
            DnsHeaderFlags.Response | DnsHeaderFlags.AuthoritativeAnswer,
            DnsOpCode.Query,
            DnsResponseCode.NoError,
            (ushort)query.Questions.Count,
            0,
            1,
            0);

        var response = new DnsMessage(
            header,
            query.Questions,
            Array.Empty<DnsRecord>(),
            new DnsRecord[] { _soa },
            Array.Empty<DnsRecord>());

        byte[] buffer = new byte[4096];
        int written = response.WriteTo(buffer);
        byte[] trimmed = new byte[written];
        Buffer.BlockCopy(buffer, 0, trimmed, 0, written);
        return trimmed;
    }

    private bool IsInZoneOrChild(DnsName name)
    {
        // True if name is the zone itself or inside it (including delegated children).
        if (_zone.Value == ".")
        {
            return true; // root authority answers everything
        }
        if (name.Equals(_zone))
        {
            return true;
        }
        return IsSubdomain(name, _zone);
    }

    private Delegation? FindMatchingDelegation(DnsName name)
    {
        // Find the most-specific delegation that covers the queried name.
        Delegation? best = null;
        foreach (Delegation d in _delegations.Values)
        {
            if (name.Equals(d.Zone) || IsSubdomain(name, d.Zone))
            {
                if (best is null || d.Zone.GetLabels().Length > best.Zone.GetLabels().Length)
                {
                    best = d;
                }
            }
        }
        return best;
    }

    private static bool IsSubdomain(DnsName name, DnsName parent)
    {
        if (parent.Value == ".")
        {
            return !name.IsRoot;
        }
        string[] nameLabels = name.GetLabels();
        string[] parentLabels = parent.GetLabels();
        if (nameLabels.Length <= parentLabels.Length)
        {
            return false;
        }
        for (int i = 1; i <= parentLabels.Length; i++)
        {
            if (!string.Equals(
                    nameLabels[nameLabels.Length - i],
                    parentLabels[parentLabels.Length - i],
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        return true;
    }

    public ValueTask DisposeAsync()
    {
        _virtualMap.TryRemove(VirtualAddress, out _);
        return _server.DisposeAsync();
    }

    /// <summary>
    /// Registers <paramref name="server"/> behind a freshly allocated virtual address so a
    /// test can wire it into the resolver via <see cref="Delegate"/> / <see cref="DelegateRaw"/>
    /// alongside <see cref="CreateTransportFactory"/>.
    /// </summary>
    public static IPEndPoint RegisterVirtual(LoopbackUdpDnsServer server)
    {
        int idx = Interlocked.Increment(ref _nextVirtualIp);
        IPAddress addr = new(new byte[] { 127, 42, (byte)(idx >> 8), (byte)(idx & 0xFF) });
        _virtualMap[addr] = server.EndPoint;
        return new IPEndPoint(addr, 53);
    }

    /// <summary>
    /// Returns a transport factory suitable for
    /// <see cref="IterativeDnsResolverOptions.UdpTransportFactory"/>. Translates virtual
    /// addresses produced by the fixture's glue records back to the real loopback ephemeral
    /// ports the authorities listen on. Endpoints not in the registry fall back to the
    /// supplied IPEndPoint unchanged (useful for negative tests).
    /// </summary>
    public static Func<IPEndPoint, DnsTransport> CreateTransportFactory()
    {
        return endpoint =>
        {
            IPEndPoint actual = _virtualMap.TryGetValue(endpoint.Address, out IPEndPoint? mapped)
                ? mapped
                : endpoint;
            return new UdpDnsTransport(new UdpDnsTransportOptions
            {
                EndPoint = actual,
                QueryTimeout = TimeSpan.FromSeconds(2),
            });
        };
    }

    private sealed class Delegation
    {
        public Delegation(DnsName zone, DnsName nsName, IPEndPoint glueEndpoint, bool includeGlue)
        {
            Zone = zone;
            NsName = nsName;
            GlueEndpoint = glueEndpoint;
            IncludeGlue = includeGlue;
        }
        public DnsName Zone { get; }
        public DnsName NsName { get; }
        public IPEndPoint GlueEndpoint { get; }
        public bool IncludeGlue { get; }
        public DnsName? GlueOwnerOverride { get; set; }
    }
}
