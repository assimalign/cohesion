using System;
using System.Linq;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Assimalign.Cohesion.Net.Cryptography.Internal;

internal readonly partial struct CertificateResult
{
    private const string MacOsCertificateSubjectRegex = "CN=(.*[^,]+).*";
    private const string MacOSSystemKeyChain = "/Library/Keychains/System.keychain";
    private const string MacOSFindCertificateCommandLine = "security";
    private const string MacOSFindCertificateCommandLineArgumentsFormat = "find-certificate -c {0} -a -Z -p " + MacOSSystemKeyChain;
    private const string MacOSFindCertificateOutputRegex = "SHA-1 hash: ([0-9A-Z]+)";
    private static readonly TimeSpan MaxRegexTimeout = TimeSpan.FromMinutes(1);

    private bool IsMacOsCertificateTrusted()
    {
        var match = Regex.Match(Certificate.Subject, MacOsCertificateSubjectRegex, RegexOptions.Singleline, MaxRegexTimeout);

        if (!match.Success)
        {
            throw new InvalidOperationException($"Can't determine the subject for the certificate with subject '{Certificate.Subject}'.");
        }

        var subject = match.Groups[1].Value;

        using var checkTrustProcess = Process.Start(
            new ProcessStartInfo(
                MacOSFindCertificateCommandLine, 
                string.Format(CultureInfo.InvariantCulture, MacOSFindCertificateCommandLineArgumentsFormat, subject))
        {
            RedirectStandardOutput = true
        });
        var output = checkTrustProcess!.StandardOutput.ReadToEnd();

        checkTrustProcess.WaitForExit();

        var matches = Regex.Matches(output, MacOSFindCertificateOutputRegex, RegexOptions.Multiline, MaxRegexTimeout);
        var hashes = matches.OfType<Match>().Select(m => m.Groups[1].Value).ToList();
        var thumbprint = Certificate.Thumbprint;

        return hashes.Any(h => string.Equals(h, thumbprint, StringComparison.Ordinal));
    }

    // Apparently there is no good way of checking on the
    // underlying implementation if ti is exportable, so just return true.
    private bool IsMacOsCertificateExportable()
    {
        return true;
    }
}