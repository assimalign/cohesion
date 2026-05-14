using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Dns;

/// <summary>
/// Sends a serialized DNS message to a single upstream server and returns the response bytes.
/// Pluggable so clients can swap UDP / TCP / DoT / DoH / DoQ without changing higher-level
/// resolver logic.
/// </summary>
/// <remarks>
/// <para>
/// The transport works in terms of raw byte buffers; serialization and parsing live in the
/// wire-format layer of <c>Assimalign.Cohesion.Dns</c>. This split keeps the transport
/// implementation small and lets the resolver decide message-level concerns like
/// EDNS payload size or DNSSEC bits without involving the network layer.
/// </para>
/// <para>
/// Implementations <strong>MUST</strong> honour the cancellation token and translate
/// network-level failures into <see cref="DnsException"/> with
/// <see cref="DnsErrorCode.Transport"/> or <see cref="DnsErrorCode.Timeout"/>.
/// </para>
/// </remarks>
public interface IDnsTransport : IDisposable, IAsyncDisposable
{
    /// <summary>
    /// Sends <paramref name="request"/> to <paramref name="endpoint"/> and returns the
    /// response payload. The returned <see cref="ReadOnlyMemory{Byte}"/> may share storage
    /// with a pooled buffer owned by the transport; callers <strong>MUST NOT</strong> retain
    /// it beyond the lifetime of the awaited task without copying.
    /// </summary>
    /// <param name="endpoint">The upstream server to query.</param>
    /// <param name="request">The serialized DNS request bytes (just the DNS message; no
    /// length-prefix for stream-based transports &#8211; the transport supplies framing).</param>
    /// <param name="cancellationToken">Cancels the in-flight exchange.</param>
    Task<ReadOnlyMemory<byte>> ExchangeAsync(
        EndPoint endpoint,
        ReadOnlyMemory<byte> request,
        CancellationToken cancellationToken = default);
}
