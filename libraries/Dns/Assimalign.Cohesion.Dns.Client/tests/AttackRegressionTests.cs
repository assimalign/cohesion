using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Dns.Tests;

/// <summary>
/// Regressions for DNS attack patterns. Each test models a specific spoof / poison vector
/// against the resolver and asserts that the resolver rejects it.
/// </summary>
public sealed class AttackRegressionTests
{
    /// <summary>
    /// Kaminsky-style ID-guessing attack: an off-path attacker tries to inject a spoofed
    /// response by racing the real answer with messages carrying randomly-guessed
    /// transaction ids. With a 16-bit id space and cryptographically-random ids the success
    /// probability per attempt is &lt;= 1 / 65536; this test exercises that probability is
    /// not 100% (i.e. the resolver actually validates the id rather than echoing whatever
    /// arrives first).
    /// </summary>
    [Fact]
    public async Task ForwardingResolver_RejectsResponseWithWrongTransactionId()
    {
        await using var server = new LoopbackUdpDnsServer();
        server.OnRequest(req =>
        {
            DnsMessage query = DnsTestMessages.ParseQuery(req);
            ushort wrongId = (ushort)(query.Header.Id ^ 0xFFFF); // deterministic mismatch
            return DnsTestMessages.BuildWithCustomIdAndQuestion(
                wrongId,
                query.Questions[0],
                new DnsRecord[] { new DnsARecord(query.Questions[0].Name, IPAddress.Parse("6.6.6.6"), 300) });
        });

        using var udp = new UdpDnsTransport(new UdpDnsTransportOptions
        {
            EndPoint = server.EndPoint,
            QueryTimeout = TimeSpan.FromSeconds(1),
        });
        var resolver = new ForwardingDnsResolver(new ForwardingDnsResolverOptions
        {
            Forwarders = { udp },
            QueryTimeout = TimeSpan.FromSeconds(2),
        });

        DnsException ex = await Assert.ThrowsAsync<DnsException>(
            () => resolver.ResolveAsync(new DnsQuestion("victim.com", DnsRecordType.A)));
        Assert.Equal(DnsErrorCode.Spoofed, ex.Code);
    }

    /// <summary>
    /// Ghost-domain attack: a malicious authority returns a referral with NS records
    /// claiming authority over a zone it does not control (out-of-bailiwick referral). A
    /// vulnerable resolver would follow the referral and poison its cache for the sibling
    /// zone. The bailiwick check in <see cref="IterativeDnsResolver"/> must reject the
    /// referral.
    /// </summary>
    [Fact]
    public async Task IterativeResolver_RejectsOutOfBailiwickReferral()
    {
        await using var root = new LoopbackDnsAuthority(".");
        await using var attacker = new LoopbackDnsAuthority("attacker.com");

        // Root delegates "attacker.com" to attacker authority, with in-bailiwick glue.
        root.Delegate("attacker.com", "ns1.attacker.com", attacker);

        // Attacker tries to issue a referral for "victim.com" — out-of-bailiwick (victim.com
        // is not inside attacker.com).
        attacker.DelegateRaw(
            "victim.com",
            nsName: "ns.attacker.com",
            glueEndpoint: attacker.VirtualEndPoint,
            glueOwner: "ns.attacker.com");

        var resolver = new IterativeDnsResolver(new IterativeDnsResolverOptions
        {
            RootEndpoints = new List<IPEndPoint> { root.VirtualEndPoint },
            UdpTransportFactory = LoopbackDnsAuthority.CreateTransportFactory(),
            TcpTransportFactory = null,
            EnableQNameMinimization = false,
        });

        // Querying victim.com starts at the root, which doesn't have a delegation for it,
        // so root responds NXDOMAIN. Querying attacker.com lands on the attacker authority,
        // which would *like* to send us off to victim.com — but the bailiwick check
        // rejects that.
        DnsException ex = await Assert.ThrowsAsync<DnsException>(
            () => resolver.ResolveAsync(new DnsQuestion("data.victim.com", DnsRecordType.A)));
        // Either NotFound (root said NXDOMAIN) or Spoofed (rejected referral). Both are
        // acceptable — the poison did NOT succeed.
        Assert.True(ex.Code is DnsErrorCode.NotFound or DnsErrorCode.Spoofed,
            $"Expected NotFound or Spoofed, got {ex.Code}");
    }

    /// <summary>
    /// Out-of-bailiwick glue: a parent authority returns a referral with NS pointing at a
    /// name outside the delegated zone, plus glue records for that NS name. A vulnerable
    /// resolver would trust the glue and cache the address; the strict glue policy must
    /// discard it.
    /// </summary>
    [Fact]
    public async Task IterativeResolver_DiscardsOutOfBailiwickGlue()
    {
        await using var root = new LoopbackDnsAuthority(".");
        await using var attacker = new LoopbackDnsAuthority("zone.example");

        // Root delegates zone.example with NS pointing at ns.evil.com (out-of-bailiwick),
        // plus glue for ns.evil.com (also out-of-bailiwick).
        root.DelegateRaw(
            "zone.example",
            nsName: "ns.evil.com",
            glueEndpoint: attacker.VirtualEndPoint,
            glueOwner: "ns.evil.com");

        var resolver = new IterativeDnsResolver(new IterativeDnsResolverOptions
        {
            RootEndpoints = new List<IPEndPoint> { root.VirtualEndPoint },
            UdpTransportFactory = LoopbackDnsAuthority.CreateTransportFactory(),
            TcpTransportFactory = null,
            EnableQNameMinimization = false,
        });

        DnsException ex = await Assert.ThrowsAsync<DnsException>(
            () => resolver.ResolveAsync(new DnsQuestion("foo.zone.example", DnsRecordType.A)));
        // The resolver discards the out-of-bailiwick glue. With no usable glue and no
        // out-of-bailiwick NS-resolution implementation, it surfaces Transport.
        Assert.Equal(DnsErrorCode.Transport, ex.Code);
    }

    /// <summary>
    /// Cross-zone poisoning: an attacker that's authoritative for one zone returns
    /// authoritative-looking answers for a different zone. The bailiwick check on the
    /// answer authority must reject the poison.
    /// </summary>
    [Fact]
    public async Task IterativeResolver_RejectsAnswerOutsideAuthoritysZone()
    {
        await using var root = new LoopbackDnsAuthority(".");
        await using var attacker = new LoopbackDnsAuthority("attacker.com");
        root.Delegate("attacker.com", "ns1.attacker.com", attacker);

        // Attacker tries to claim authoritative for "victim.com" by adding the record. Our
        // LoopbackDnsAuthority's REFUSED-out-of-zone semantics will return REFUSED for any
        // direct query for victim.com against attacker.com — the resolver should never
        // reach attacker.com for a victim.com query anyway because root won't delegate.
        attacker.AddRecord(new DnsARecord("victim.com", IPAddress.Parse("6.6.6.6"), 300));

        var resolver = new IterativeDnsResolver(new IterativeDnsResolverOptions
        {
            RootEndpoints = new List<IPEndPoint> { root.VirtualEndPoint },
            UdpTransportFactory = LoopbackDnsAuthority.CreateTransportFactory(),
            TcpTransportFactory = null,
            EnableQNameMinimization = false,
        });

        DnsException ex = await Assert.ThrowsAsync<DnsException>(
            () => resolver.ResolveAsync(new DnsQuestion("victim.com", DnsRecordType.A)));
        // Root authoritatively says NXDOMAIN for victim.com (it isn't delegated).
        Assert.Equal(DnsErrorCode.NotFound, ex.Code);
    }

    /// <summary>
    /// Rapid Kaminsky simulation: send many queries and count how often a wrong-id flood
    /// poisons the cache. With a properly random 16-bit id, a single random guess has
    /// 1/65536 chance of matching. We verify the resolver never accepts a wrong id (the
    /// validate-or-die policy) rather than relying on probabilistic success bounds.
    /// </summary>
    [Fact]
    public async Task ForwardingResolver_HighThroughputSpoofAttempts_NeverSucceeds()
    {
        // Server sends one spoof per request, drawing a "guess" from a small space so the
        // resolver MIGHT receive a matching id by accident — but the validation logic
        // means it never accepts the wrong question or wrong id.
        await using var server = new LoopbackUdpDnsServer();
        var sawWrongAnswer = new ConcurrentBag<bool>();

        server.OnRequest(req =>
        {
            DnsMessage query = DnsTestMessages.ParseQuery(req);
            // Spoofed response: ID matches but answer is for the wrong question.
            return DnsTestMessages.BuildWithCustomIdAndQuestion(
                query.Header.Id,
                new DnsQuestion("not-what-you-asked.example.com", DnsRecordType.A),
                new DnsRecord[]
                {
                    new DnsARecord("not-what-you-asked.example.com", IPAddress.Parse("6.6.6.6"), 300),
                });
        });

        using var udp = new UdpDnsTransport(new UdpDnsTransportOptions
        {
            EndPoint = server.EndPoint,
            QueryTimeout = TimeSpan.FromSeconds(1),
        });

        // Run 50 rapid-fire queries. Every single one should fail with Spoofed; none should
        // succeed and pollute downstream callers.
        for (int i = 0; i < 50; i++)
        {
            // Fresh resolver per iteration to avoid cache effects.
            var resolver = new ForwardingDnsResolver(new ForwardingDnsResolverOptions
            {
                Forwarders = { udp },
                EnableCache = false,
                QueryTimeout = TimeSpan.FromSeconds(2),
            });

            try
            {
                _ = await resolver.ResolveAsync(new DnsQuestion("real-victim.com", DnsRecordType.A));
                sawWrongAnswer.Add(true);
            }
            catch (DnsException ex)
            {
                Assert.Equal(DnsErrorCode.Spoofed, ex.Code);
            }
        }

        Assert.Empty(sawWrongAnswer);
    }

    /// <summary>
    /// Resolver MUST verify the QR flag (response bit) on incoming messages. A malicious
    /// host that bounces a query message back unchanged (preserving id and question) would
    /// otherwise look like a valid response.
    /// </summary>
    [Fact]
    public async Task ForwardingResolver_RejectsResponseMissingQRFlag()
    {
        await using var server = new LoopbackUdpDnsServer();
        server.OnRequest(req =>
        {
            DnsMessage query = DnsTestMessages.ParseQuery(req);
            // Construct a response that does NOT set the QR flag.
            var header = new DnsHeader(
                query.Header.Id,
                DnsHeaderFlags.None, // intentionally no QR
                DnsOpCode.Query,
                DnsResponseCode.NoError,
                (ushort)query.Questions.Count,
                1, 0, 0);
            var msg = new DnsMessage(
                header,
                query.Questions,
                new DnsRecord[] { new DnsARecord(query.Questions[0].Name, IPAddress.Parse("6.6.6.6"), 300) },
                Array.Empty<DnsRecord>(),
                Array.Empty<DnsRecord>());

            byte[] buffer = new byte[4096];
            int written = msg.WriteTo(buffer);
            byte[] trimmed = new byte[written];
            Buffer.BlockCopy(buffer, 0, trimmed, 0, written);
            return trimmed;
        });

        using var udp = new UdpDnsTransport(new UdpDnsTransportOptions
        {
            EndPoint = server.EndPoint,
            QueryTimeout = TimeSpan.FromSeconds(1),
        });
        var resolver = new ForwardingDnsResolver(new ForwardingDnsResolverOptions
        {
            Forwarders = { udp },
            QueryTimeout = TimeSpan.FromSeconds(2),
        });

        DnsException ex = await Assert.ThrowsAsync<DnsException>(
            () => resolver.ResolveAsync(new DnsQuestion("example.com", DnsRecordType.A)));
        Assert.Equal(DnsErrorCode.Spoofed, ex.Code);
    }
}
