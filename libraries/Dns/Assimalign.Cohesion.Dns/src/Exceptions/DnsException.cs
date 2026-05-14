using System;
using System.Diagnostics.CodeAnalysis;

namespace Assimalign.Cohesion.Dns;

/// <summary>
/// Domain exception raised by every Cohesion DNS provider (client, resolver, authority,
/// transport). Carries a <see cref="DnsErrorCode"/> so callers can branch on the failure
/// category without text-matching the message, plus an optional wire <see cref="DnsResponseCode"/>
/// when the failure originated in an upstream RCODE.
/// </summary>
/// <remarks>
/// <para>
/// Static <see cref="Throw"/>* helpers are marked <see cref="DoesNotReturnAttribute"/> so they
/// can be used inline at branch points without confusing the flow analyzer.
/// </para>
/// </remarks>
public class DnsException : Exception
{
    /// <summary>
    /// Initializes a new <see cref="DnsException"/> with <see cref="DnsErrorCode.Other"/>.
    /// </summary>
    public DnsException(string message)
        : this(DnsErrorCode.Other, message)
    {
    }

    /// <summary>
    /// Initializes a new <see cref="DnsException"/> with the supplied <paramref name="code"/>.
    /// </summary>
    public DnsException(DnsErrorCode code, string message)
        : base(message)
    {
        Code = code;
    }

    /// <summary>
    /// Initializes a new <see cref="DnsException"/> with the supplied <paramref name="code"/>
    /// and inner exception.
    /// </summary>
    public DnsException(DnsErrorCode code, string message, Exception? innerException)
        : base(message, innerException)
    {
        Code = code;
    }

    /// <summary>
    /// Diagnostic category attached to the exception. Callers SHOULD branch on this rather
    /// than the message text.
    /// </summary>
    public virtual DnsErrorCode Code { get; } = DnsErrorCode.Other;

    /// <summary>
    /// The wire-level <see cref="DnsResponseCode"/> when the failure originated in an upstream
    /// RCODE; <see langword="null"/> otherwise.
    /// </summary>
    public virtual DnsResponseCode? ResponseCode { get; init; }

    /// <summary>
    /// Throws a <see cref="DnsException"/> with <see cref="DnsErrorCode.NotFound"/>.
    /// </summary>
    [DoesNotReturn]
    public static void ThrowNotFound(string question, Exception? innerException = null)
    {
        throw new DnsException(
            DnsErrorCode.NotFound,
            $"The requested DNS name was not found: '{question}'.",
            innerException)
        {
            ResponseCode = DnsResponseCode.NXDomain,
        };
    }

    /// <summary>
    /// Throws a <see cref="DnsException"/> with <see cref="DnsErrorCode.ServerFailure"/> and
    /// records the upstream <paramref name="responseCode"/>.
    /// </summary>
    [DoesNotReturn]
    public static void ThrowServerFailure(string question, DnsResponseCode responseCode, Exception? innerException = null)
    {
        throw new DnsException(
            DnsErrorCode.ServerFailure,
            $"The DNS server reported {responseCode} for '{question}'.",
            innerException)
        {
            ResponseCode = responseCode,
        };
    }

    /// <summary>
    /// Throws a <see cref="DnsException"/> with <see cref="DnsErrorCode.Timeout"/>.
    /// </summary>
    [DoesNotReturn]
    public static void ThrowTimeout(string question, Exception? innerException = null)
        => throw new DnsException(
            DnsErrorCode.Timeout,
            $"The DNS query for '{question}' did not receive a response within the configured timeout.",
            innerException);

    /// <summary>
    /// Throws a <see cref="DnsException"/> with <see cref="DnsErrorCode.Malformed"/>.
    /// </summary>
    [DoesNotReturn]
    public static void ThrowMalformed(string detail, Exception? innerException = null)
        => throw new DnsException(
            DnsErrorCode.Malformed,
            $"The DNS message could not be parsed: {detail}.",
            innerException);

    /// <summary>
    /// Throws a <see cref="DnsException"/> with <see cref="DnsErrorCode.Spoofed"/>.
    /// </summary>
    [DoesNotReturn]
    public static void ThrowSpoofed(string detail, Exception? innerException = null)
        => throw new DnsException(
            DnsErrorCode.Spoofed,
            $"The DNS response did not match the outgoing query and may have been spoofed: {detail}.",
            innerException);

    /// <summary>
    /// Throws a <see cref="DnsException"/> with <see cref="DnsErrorCode.DnssecValidationFailed"/>.
    /// </summary>
    [DoesNotReturn]
    public static void ThrowDnssecValidationFailed(string detail, Exception? innerException = null)
        => throw new DnsException(
            DnsErrorCode.DnssecValidationFailed,
            $"DNSSEC validation failed: {detail}.",
            innerException);

    /// <summary>
    /// Throws a <see cref="DnsException"/> with <see cref="DnsErrorCode.Transport"/>.
    /// </summary>
    [DoesNotReturn]
    public static void ThrowTransport(string detail, Exception? innerException = null)
        => throw new DnsException(
            DnsErrorCode.Transport,
            $"DNS transport failure: {detail}.",
            innerException);

    /// <summary>
    /// Throws a <see cref="DnsException"/> with <see cref="DnsErrorCode.ReadOnly"/>.
    /// </summary>
    [DoesNotReturn]
    public static void ThrowReadOnly(string operation)
    {
        string message = string.IsNullOrEmpty(operation)
            ? "The operation is not allowed; the DNS target is read-only."
            : $"The operation '{operation}' is not allowed; the DNS target is read-only.";
        throw new DnsException(DnsErrorCode.ReadOnly, message);
    }

    /// <summary>
    /// Throws a <see cref="DnsException"/> with <see cref="DnsErrorCode.TsigVerificationFailed"/>.
    /// </summary>
    [DoesNotReturn]
    public static void ThrowTsigVerificationFailed(string detail, Exception? innerException = null)
        => throw new DnsException(
            DnsErrorCode.TsigVerificationFailed,
            $"TSIG verification failed: {detail}.",
            innerException);
}
