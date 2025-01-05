using System;
using System.Security.Cryptography.X509Certificates;

namespace Assimalign.Cohesion.Net.Cryptography.Internal;

internal readonly partial struct CertificateResult
{
    private readonly static bool isWindows = OperatingSystem.IsWindows();
    private readonly static bool isMacOs = OperatingSystem.IsMacOS();
    private readonly static bool isUnix = OperatingSystem.IsLinux();

    public CertificateResult(X509Certificate2 certificate)
    {
        this.Certificate = certificate;
    }
    
    public X509Certificate2 Certificate { get; }

    public bool IsValid => IsActive && IsExportable;
    public bool IsActive => DateTimeOffset.Now >= Certificate.NotBefore && !IsExpired;
    public bool IsExpired => DateTimeOffset.Now < Certificate.NotAfter;
    public bool IsExportable
    {
        get
        {
            if (isWindows)
            {
                return IsWindowsCertificateExportable();
            }
            if (isUnix)
            {
                return IsLinuxCertificateExportable();
            }
            if (isMacOs)
            {
                return IsMacOsCertificateExportable();
            }

            throw new PlatformNotSupportedException();
        }
    }
    public bool IsTrusted
    {
        get
        {
            if (isWindows)
            {
                return IsWindowsCertificateTrusted();
            }
            if (isUnix)
            {
                return IsLinuxCertificateTrusted();
            }
            if (isMacOs)
            {
                return IsMacOsCertificateTrusted();
            }

            throw new PlatformNotSupportedException();
        }
    }

    
    public static implicit operator CertificateResult(X509Certificate2 certificate) => new CertificateResult(certificate);
    public static implicit operator X509Certificate2(CertificateResult certificateResult) => certificateResult.Certificate;
}