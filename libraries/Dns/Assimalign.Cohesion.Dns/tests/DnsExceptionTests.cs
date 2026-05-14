using System;
using Assimalign.Cohesion.Dns;

namespace Assimalign.Cohesion.Dns.Tests;

/// <summary>
/// Covers the <see cref="DnsException"/> static <c>Throw*</c> helpers and the
/// <see cref="DnsErrorCode"/> ordinal stability contract.
/// </summary>
public class DnsExceptionTests
{
    [Fact(DisplayName = "Cohesion Test [Dns] - Exception: ThrowNotFound maps to NotFound + NXDomain")]
    public void ThrowNotFound_Mapping()
    {
        var ex = Assert.Throws<DnsException>(() => DnsException.ThrowNotFound("example.com"));
        Assert.Equal(DnsErrorCode.NotFound, ex.Code);
        Assert.Equal(DnsResponseCode.NXDomain, ex.ResponseCode);
        Assert.Contains("example.com", ex.Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Cohesion Test [Dns] - Exception: ThrowServerFailure carries the RCODE")]
    public void ThrowServerFailure_CarriesResponseCode()
    {
        var ex = Assert.Throws<DnsException>(
            () => DnsException.ThrowServerFailure("example.com", DnsResponseCode.ServFail));
        Assert.Equal(DnsErrorCode.ServerFailure, ex.Code);
        Assert.Equal(DnsResponseCode.ServFail, ex.ResponseCode);
    }

    [Fact(DisplayName = "Cohesion Test [Dns] - Exception: ThrowTimeout maps to Timeout")]
    public void ThrowTimeout_Mapping()
    {
        var ex = Assert.Throws<DnsException>(() => DnsException.ThrowTimeout("example.com"));
        Assert.Equal(DnsErrorCode.Timeout, ex.Code);
        Assert.Null(ex.ResponseCode);
    }

    [Fact(DisplayName = "Cohesion Test [Dns] - Exception: ThrowMalformed wraps inner exception")]
    public void ThrowMalformed_WrapsInner()
    {
        var inner = new InvalidOperationException("bad header");
        var ex = Assert.Throws<DnsException>(() => DnsException.ThrowMalformed("bad header", inner));
        Assert.Equal(DnsErrorCode.Malformed, ex.Code);
        Assert.Same(inner, ex.InnerException);
    }

    [Fact(DisplayName = "Cohesion Test [Dns] - Exception: ThrowSpoofed maps to Spoofed")]
    public void ThrowSpoofed_Mapping()
    {
        var ex = Assert.Throws<DnsException>(() => DnsException.ThrowSpoofed("transaction id mismatch"));
        Assert.Equal(DnsErrorCode.Spoofed, ex.Code);
    }

    [Fact(DisplayName = "Cohesion Test [Dns] - Exception: ThrowDnssecValidationFailed maps to DnssecValidationFailed")]
    public void ThrowDnssecValidationFailed_Mapping()
    {
        var ex = Assert.Throws<DnsException>(() => DnsException.ThrowDnssecValidationFailed("expired RRSIG"));
        Assert.Equal(DnsErrorCode.DnssecValidationFailed, ex.Code);
    }

    [Fact(DisplayName = "Cohesion Test [Dns] - Exception: ThrowTransport maps to Transport")]
    public void ThrowTransport_Mapping()
    {
        var ex = Assert.Throws<DnsException>(() => DnsException.ThrowTransport("connection refused"));
        Assert.Equal(DnsErrorCode.Transport, ex.Code);
    }

    [Fact(DisplayName = "Cohesion Test [Dns] - Exception: ThrowReadOnly maps to ReadOnly")]
    public void ThrowReadOnly_Mapping()
    {
        var ex = Assert.Throws<DnsException>(() => DnsException.ThrowReadOnly("Update"));
        Assert.Equal(DnsErrorCode.ReadOnly, ex.Code);
        Assert.Contains("Update", ex.Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Cohesion Test [Dns] - Exception: ThrowTsigVerificationFailed maps to TsigVerificationFailed")]
    public void ThrowTsigVerificationFailed_Mapping()
    {
        var ex = Assert.Throws<DnsException>(
            () => DnsException.ThrowTsigVerificationFailed("bad signature"));
        Assert.Equal(DnsErrorCode.TsigVerificationFailed, ex.Code);
    }

    [Fact(DisplayName = "Cohesion Test [Dns] - ErrorCode: ordinal values are stable")]
    public void ErrorCode_OrdinalStability()
    {
        // The numeric values are part of the public contract. Changing them is a breaking
        // change. This test pins the ordering so an accidental enum reorder fails CI.
        Assert.Equal(0, (int)DnsErrorCode.Other);
        Assert.Equal(1, (int)DnsErrorCode.NotFound);
        Assert.Equal(2, (int)DnsErrorCode.ServerFailure);
        Assert.Equal(3, (int)DnsErrorCode.Timeout);
        Assert.Equal(4, (int)DnsErrorCode.Malformed);
        Assert.Equal(5, (int)DnsErrorCode.Spoofed);
        Assert.Equal(6, (int)DnsErrorCode.DnssecValidationFailed);
        Assert.Equal(7, (int)DnsErrorCode.Transport);
        Assert.Equal(8, (int)DnsErrorCode.ReadOnly);
        Assert.Equal(9, (int)DnsErrorCode.TsigVerificationFailed);
    }
}
