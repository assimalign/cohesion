namespace Assimalign.Cohesion.Net.Cryptography.Internal;

internal readonly partial struct CertificateResult
{
    private bool IsLinuxCertificateExportable()
    {
        return true;
    }

    private bool IsLinuxCertificateTrusted()
    {
        return false;
    }
}
