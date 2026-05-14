using System;
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
/// The client does not take ownership of the transport: the caller is responsible for
/// disposing the transport instance. Disposing the client itself is a no-op beyond marking
/// it as disposed.
/// </para>
/// </remarks>
public sealed class StubDnsClient : DnsClient
{
    private readonly StubDnsClientOptions _options;

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

        _options = options;
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

        DnsMessage query = DnsQueryHelper.BuildQuery(
            DnsQueryHelper.NewTransactionId(),
            question,
            recursionDesired: _options.RecursionDesired,
            ednsPayloadSize: _options.EdnsPayloadSize);

        DnsMessage response = await DnsQueryHelper.ExchangeAsync(
            _options.Transport!,
            query,
            timeoutCts,
            cancellationToken).ConfigureAwait(false);

        return ThrowIfNonSuccess(response, question);
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
