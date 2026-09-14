using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Net.Security;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

using Assimalign.Cohesion.Core;

namespace Assimalign.Cohesion.Hosting.Resources;

public sealed partial class ResourceContext
{
    /// <summary>Reads an endpoint's certificate and matching private key from its Secret mount.</summary>
    /// <param name="endpointName">The logical endpoint name.</param>
    /// <param name="certificate">The certificate on success; the caller owns and disposes it.</param>
    /// <returns>False when the mount is absent or empty; otherwise true.</returns>
    /// <exception cref="ArgumentException">The endpoint name is empty.</exception>
    /// <exception cref="IOException">The present mount cannot be read.</exception>
    /// <exception cref="CryptographicException">The PEM material cannot be loaded.</exception>
    /// <exception cref="InvalidOperationException">The bundle has an invalid key count or validity window.</exception>
    public bool TryGetEndpointCertificate(string endpointName, [NotNullWhen(true)] out X509Certificate2? certificate)
    {
        bool found = TryGetEndpointCertificate(endpointName, out certificate, out X509Certificate2Collection chain);
        foreach (X509Certificate2 issuer in chain)
        {
            issuer.Dispose();
        }
        return found;
    }

    /// <summary>Reads an endpoint's certificate, private key, and issuer chain without requiring a PEM block order.</summary>
    /// <param name="endpointName">The logical endpoint name.</param>
    /// <param name="certificate">The leaf with its private key; the caller owns and disposes it.</param>
    /// <param name="chain">Additional certificates, including any supplied root; the caller disposes each.</param>
    /// <returns>False when the mount is absent or empty; otherwise true.</returns>
    /// <exception cref="ArgumentException">The endpoint name is empty.</exception>
    /// <exception cref="IOException">The present mount cannot be read.</exception>
    /// <exception cref="CryptographicException">The PEM material cannot be loaded.</exception>
    /// <exception cref="InvalidOperationException">The bundle has an invalid key count or validity window.</exception>
    public bool TryGetEndpointCertificate(
        string endpointName,
        [NotNullWhen(true)] out X509Certificate2? certificate,
        out X509Certificate2Collection chain) =>
        TryGetEndpointCertificate(endpointName, null, out certificate, out chain);

    /// <summary>Reads a configured endpoint's certificate using an explicit mount override when supplied.</summary>
    /// <param name="endpointName">The logical endpoint name.</param>
    /// <param name="certificateMount">An explicit Secret mount name, or null to use registered endpoint metadata.</param>
    /// <param name="certificate">The leaf with its private key; the caller owns and disposes it.</param>
    /// <param name="chain">Additional certificates, including any supplied root; the caller disposes each.</param>
    /// <returns>False for an absent or empty mount or the reserved public certificate; otherwise true.</returns>
    /// <exception cref="ArgumentException">The endpoint name is empty.</exception>
    /// <exception cref="IOException">The present mount cannot be read.</exception>
    /// <exception cref="CryptographicException">The PEM material cannot be loaded.</exception>
    /// <exception cref="InvalidOperationException">The bundle has an invalid key count or validity window.</exception>
    public bool TryGetEndpointCertificate(
        string endpointName,
        string? certificateMount,
        [NotNullWhen(true)] out X509Certificate2? certificate,
        out X509Certificate2Collection chain)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointName);
        certificate = null;
        chain = new X509Certificate2Collection();
        string name = certificateMount ?? (_endpointCertificates.TryGetValue(endpointName, out string? mapped) ? mapped : "tls");
        if (string.IsNullOrEmpty(name) || string.Equals(name, "public", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (!_mounts.TryGetValue(name, out ResourceMount? mount))
        {
            if (!ResourceEnvironment.TryGetMount(_environmentVariables, name, out string? path))
            {
                return false;
            }
            mount = new ResourceMount(path);
        }

        byte[] bytes = mount.ReadAllBytes();
        char[] pem = new char[Encoding.UTF8.GetCharCount(bytes)];
        var parsedChain = new X509Certificate2Collection();
        try
        {
            if (bytes.Length == 0)
            {
                return false;
            }
            Encoding.UTF8.GetChars(bytes, pem);
            int keys = CountPrivateKeys(pem);
            if (keys != 1)
            {
                throw new InvalidOperationException($"Endpoint '{endpointName}' certificate Secret mount '{name}' must contain exactly one private-key block; found {keys}.");
            }
            using X509Certificate2 parsed = X509Certificate2.CreateFromPem(pem, pem);
            byte[] pkcs12 = parsed.Export(X509ContentType.Pkcs12);
            try
            {
                // The PKCS#12 import gives SslStream a Windows-compatible private-key association.
                certificate = X509CertificateLoader.LoadPkcs12(pkcs12, null, X509KeyStorageFlags.Exportable);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(pkcs12);
            }
            DateTime now = DateTime.UtcNow;
            if (!certificate.HasPrivateKey || certificate.NotBefore.ToUniversalTime() > now || certificate.NotAfter.ToUniversalTime() <= now)
            {
                throw new InvalidOperationException($"Endpoint '{endpointName}' certificate Secret mount '{name}' requires a currently valid leaf and matching private key.");
            }
            parsedChain.ImportFromPem(pem);
            foreach (X509Certificate2 issuer in parsedChain)
            {
                if (!issuer.RawDataMemory.Span.SequenceEqual(certificate.RawDataMemory.Span))
                {
                    chain.Add(X509CertificateLoader.LoadCertificate(issuer.RawData));
                }
            }
            return true;
        }
        catch
        {
            certificate?.Dispose();
            certificate = null;
            foreach (X509Certificate2 issuer in chain)
            {
                issuer.Dispose();
            }
            chain.Clear();
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            Array.Clear(pem);
            foreach (X509Certificate2 issuer in parsedChain)
            {
                issuer.Dispose();
            }
        }
    }

    /// <summary>Reads the gateway's protected transport trust-anchor bundle.</summary>
    /// <param name="anchors">The trust certificates; the caller owns and disposes each certificate.</param>
    /// <returns>False when no bundle or an empty bundle was supplied; otherwise true.</returns>
    /// <exception cref="IOException">The supplied bundle cannot be read.</exception>
    /// <exception cref="CryptographicException">The PEM certificates cannot be loaded.</exception>
    /// <exception cref="InvalidOperationException">The bundle includes a private key or no certificates.</exception>
    public bool TryGetTrustBundle(out X509Certificate2Collection anchors)
    {
        anchors = new X509Certificate2Collection();
        string? path = GetEnvironmentValue(ResourceEnvironment.TrustBundlePath);
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }
        byte[] bytes = new ResourceMount(path).ReadAllBytes();
        char[] pem = new char[Encoding.UTF8.GetCharCount(bytes)];
        try
        {
            if (bytes.Length == 0)
            {
                return false;
            }
            Encoding.UTF8.GetChars(bytes, pem);
            if (CountPrivateKeys(pem) != 0)
            {
                throw new InvalidOperationException("The transport trust bundle must contain certificates only, without private keys.");
            }
            anchors.ImportFromPem(pem);
            if (anchors.Count == 0)
            {
                throw new InvalidOperationException("The transport trust bundle contains no certificates.");
            }
            return true;
        }
        catch
        {
            foreach (X509Certificate2 anchor in anchors)
            {
                anchor.Dispose();
            }
            anchors.Clear();
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            Array.Clear(pem);
        }
    }

    /// <summary>Creates an outbound TLS validator using the gateway's trust anchors while preserving hostname validation.</summary>
    /// <returns>A validator, or null when the gateway supplied no transport trust bundle.</returns>
    /// <exception cref="IOException">The supplied bundle cannot be read.</exception>
    /// <exception cref="CryptographicException">The PEM certificates cannot be loaded.</exception>
    /// <exception cref="InvalidOperationException">The trust bundle is invalid.</exception>
    public RemoteCertificateValidationCallback? CreateOutboundTrustValidator()
    {
        if (!TryGetTrustBundle(out X509Certificate2Collection anchors))
        {
            return null;
        }
        var encoded = new List<byte[]>(anchors.Count);
        foreach (X509Certificate2 anchor in anchors)
        {
            encoded.Add(anchor.RawData);
            anchor.Dispose();
        }
        return (_, certificate, suppliedChain, errors) =>
        {
            if (certificate is null || (errors & (SslPolicyErrors.RemoteCertificateNotAvailable | SslPolicyErrors.RemoteCertificateNameMismatch)) != 0)
            {
                return false;
            }
            if (errors == SslPolicyErrors.None)
            {
                return true;
            }
            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.DisableCertificateDownloads = true;
            chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));
            var loadedAnchors = new List<X509Certificate2>(encoded.Count);
            try
            {
                foreach (byte[] data in encoded)
                {
                    X509Certificate2 anchor = X509CertificateLoader.LoadCertificate(data);
                    loadedAnchors.Add(anchor);
                    chain.ChainPolicy.CustomTrustStore.Add(anchor);
                }
                if (suppliedChain is not null)
                {
                    foreach (X509ChainElement element in suppliedChain.ChainElements)
                    {
                        chain.ChainPolicy.ExtraStore.Add(element.Certificate);
                    }
                }
                using X509Certificate2 leaf = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
                return chain.Build(leaf);
            }
            finally
            {
                foreach (X509Certificate2 anchor in loadedAnchors)
                {
                    anchor.Dispose();
                }
            }
        };
    }

    /// <summary>Creates a temporary self-signed server identity for loopback Development hosting.</summary>
    /// <param name="host">A loopback IP address or localhost.</param>
    /// <returns>The certificate and private key, owned by the caller.</returns>
    /// <exception cref="InvalidOperationException">The context is not loopback Development.</exception>
    public X509Certificate2 CreateDevelopmentEndpointCertificate(string host)
    {
        bool localhost = string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase);
        bool loopback = localhost || (IPAddress.TryParse(host, out IPAddress? address) && IPAddress.IsLoopback(address));
        if (!loopback || !string.Equals(EnvironmentName, "Development", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Development endpoint certificates require a loopback Development resource.");
        }
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={host}", key, HashAlgorithmName.SHA256);
        var names = new SubjectAlternativeNameBuilder();
        if (IPAddress.TryParse(host, out IPAddress? ip))
        {
            names.AddIpAddress(ip);
        }
        else
        {
            names.AddDnsName(host);
        }
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        using X509Certificate2 ephemeral = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(1));
        byte[] pkcs12 = ephemeral.Export(X509ContentType.Pkcs12);
        try
        {
            return X509CertificateLoader.LoadPkcs12(pkcs12, null, X509KeyStorageFlags.Exportable);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs12);
        }
    }

    internal void SetEndpointCertificates(IReadOnlyDictionary<string, string> certificates)
    {
        foreach ((string endpoint, string mount) in certificates)
        {
            _endpointCertificates.TryAdd(endpoint, mount);
        }
    }

    private static int CountPrivateKeys(ReadOnlySpan<char> pem)
    {
        int count = 0;
        while (true)
        {
            int begin = pem.IndexOf("-----BEGIN ", StringComparison.Ordinal);
            if (begin < 0)
            {
                return count;
            }
            pem = pem[(begin + 11)..];
            int end = pem.IndexOf("-----", StringComparison.Ordinal);
            if (end < 0)
            {
                return count;
            }
            if (pem[..end].EndsWith("PRIVATE KEY", StringComparison.Ordinal))
            {
                count++;
            }
            pem = pem[(end + 5)..];
        }
    }
}
