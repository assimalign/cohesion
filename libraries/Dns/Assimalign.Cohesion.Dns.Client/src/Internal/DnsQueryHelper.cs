using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Dns.Internal;

/// <summary>
/// Shared helpers for constructing DNS queries, exchanging them with a
/// <see cref="DnsTransport"/>, and validating the response per RFC 5452.
/// </summary>
/// <remarks>
/// Centralized so every Cohesion DNS client (stub, forwarding, iterative) uses the same
/// transaction-id generator, the same EDNS opt construction, and the same spoof checks. Adding
/// a new client should not be an opportunity to subtly relax any of these.
/// </remarks>
internal static class DnsQueryHelper
{
    /// <summary>
    /// Builds an outgoing DNS query for <paramref name="question"/>.
    /// </summary>
    /// <param name="id">Transaction id (caller-supplied so spies can verify the exact bytes).</param>
    /// <param name="question">Question to ask.</param>
    /// <param name="recursionDesired">Sets the RD flag. <c>true</c> for forwarding, <c>false</c>
    /// for iterative queries against authoritative servers.</param>
    /// <param name="ednsPayloadSize">EDNS UDP payload size to advertise via OPT; pass zero to
    /// omit the OPT record.</param>
    /// <param name="ednsOptions">Optional list of EDNS options to attach to the OPT record
    /// (cookies, ECS, padding, etc.). Ignored when <paramref name="ednsPayloadSize"/> is zero.</param>
    public static DnsMessage BuildQuery(
        ushort id,
        DnsQuestion question,
        bool recursionDesired,
        ushort ednsPayloadSize,
        IReadOnlyList<DnsEdnsOption>? ednsOptions = null)
    {
        bool useEdns = ednsPayloadSize > 0;
        DnsHeaderFlags flags = recursionDesired ? DnsHeaderFlags.RecursionDesired : DnsHeaderFlags.None;

        var header = new DnsHeader(
            id,
            flags,
            DnsOpCode.Query,
            DnsResponseCode.NoError,
            questionCount: 1,
            answerCount: 0,
            authorityCount: 0,
            additionalCount: useEdns ? (ushort)1 : (ushort)0);

        IReadOnlyList<DnsRecord> additionals;
        if (useEdns)
        {
            DnsOptRecord opt = ednsOptions is { Count: > 0 }
                ? new DnsOptRecord(ednsPayloadSize, extendedRCodeHigh: 0, version: 0, DnsEdnsFlags.None, ednsOptions)
                : new DnsOptRecord(ednsPayloadSize);
            additionals = new DnsRecord[] { opt };
        }
        else
        {
            additionals = Array.Empty<DnsRecord>();
        }

        return new DnsMessage(
            header,
            new[] { question },
            Array.Empty<DnsRecord>(),
            Array.Empty<DnsRecord>(),
            additionals);
    }

    /// <summary>
    /// Generates a cryptographically random 16-bit transaction id. RFC 5452 §9 calls for "a
    /// strong random source" so off-path attackers cannot predict ids and forge responses.
    /// </summary>
    public static ushort NewTransactionId()
        => (ushort)RandomNumberGenerator.GetInt32(0, 65_536);

    /// <summary>
    /// Returns the 12-bit RCODE that combines the 4-bit value in the header with the
    /// 8-bit upper byte in the OPT TTL. RFC 6891 &#167; 6.1.3: extended RCODEs (BADVERS,
    /// BADCOOKIE, &#8230;) split across these two fields, so reading just the header gives
    /// the wrong answer for any RCODE &#8805; 16.
    /// </summary>
    public static DnsResponseCode EffectiveRcode(DnsMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        int low = (int)message.Header.ResponseCode & 0x0F;
        int high = message.Edns?.ExtendedRCodeHigh ?? 0;
        return (DnsResponseCode)((high << 4) | low);
    }

    /// <summary>
    /// Serializes a query into a byte array of exactly the produced length.
    /// </summary>
    public static byte[] SerializeQuery(DnsMessage query)
    {
        // 1232 octets is the modern guidance for UDP-safe DNS messages (RFC 6891 §6.2.4 +
        // dnsflagday.net). Queries are always small relative to that bound.
        byte[] buffer = new byte[1232];
        int written = query.WriteTo(buffer);
        byte[] trimmed = new byte[written];
        Buffer.BlockCopy(buffer, 0, trimmed, 0, written);
        return trimmed;
    }

    /// <summary>
    /// Validates a response against the originating query per RFC 5452: the id must match, the
    /// QR flag must be set, the question count must match, and each question must echo
    /// (name + type + class). Failures surface as <see cref="DnsErrorCode.Spoofed"/>.
    /// </summary>
    public static void ValidateResponse(DnsMessage response, DnsMessage query)
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

    /// <summary>
    /// Sends <paramref name="query"/> through <paramref name="transport"/>, parses the
    /// response, validates it against the query, and returns the parsed message.
    /// </summary>
    /// <remarks>
    /// Translates <see cref="OperationCanceledException"/> caused by the internal timeout into
    /// <see cref="DnsErrorCode.Timeout"/>; cancellation that originates with the external token
    /// propagates unchanged.
    /// </remarks>
    public static async Task<DnsMessage> ExchangeAsync(
        DnsTransport transport,
        DnsMessage query,
        CancellationTokenSource timeoutCts,
        CancellationToken externalToken)
    {
        byte[] requestBytes = SerializeQuery(query);

        ReadOnlyMemory<byte> responseBytes;
        try
        {
            responseBytes = await transport.ExchangeAsync(requestBytes, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !externalToken.IsCancellationRequested)
        {
            DnsException.ThrowTimeout($"{query.Questions[0].Name} {query.Questions[0].Type}");
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
            DnsException.ThrowMalformed(
                $"failed to parse response for {query.Questions[0].Name} {query.Questions[0].Type}",
                ex);
            throw; // unreachable
        }

        ValidateResponse(response, query);
        return response;
    }
}
