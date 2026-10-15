using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Dns.Tests;

internal sealed class SnapshotDnsTransport : DnsTransport
{
    private readonly Func<DnsMessage, byte[]> _respond;

    internal SnapshotDnsTransport(Func<DnsMessage, byte[]> respond)
    {
        _respond = respond;
    }

    public override EndPoint Endpoint => new IPEndPoint(IPAddress.Loopback, 53);

    public override Task<ReadOnlyMemory<byte>> ExchangeAsync(ReadOnlyMemory<byte> request, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<ReadOnlyMemory<byte>>(_respond(DnsMessage.Parse(request.Span)));
    }
}
