using System;

using Assimalign.Cohesion.Security.DataProtection;

namespace Assimalign.Cohesion.Web.Authentication.Cookie.Tests.TestObjects;

internal sealed class DiagnosticDataProtector : IDataProtector
{
    private readonly DataProtectionException? _failure;

    public DiagnosticDataProtector(DataProtectionException? failure = null) => _failure = failure;

    public IDataProtector CreateProtector(string purpose) => this;
    public byte[] Protect(ReadOnlySpan<byte> plaintext) => plaintext.ToArray();
    public byte[] Unprotect(ReadOnlySpan<byte> protectedData)
        => _failure is null ? protectedData.ToArray() : throw _failure;
}
