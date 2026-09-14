using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

using Assimalign.Cohesion.Core;
using Assimalign.Cohesion.Hosting.Resources;

using HostingMount = Assimalign.Cohesion.Hosting.Resources.ResourceMount;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

internal sealed class GatewayCertificateAuthority
{
    private readonly string _directory;
    private readonly ApplicationName _application;
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _anchors = new(StringComparer.Ordinal);

    internal GatewayCertificateAuthority(string applicationDirectory, ApplicationName application)
    {
        _directory = Path.Combine(applicationDirectory, ".state", "certs");
        _application = application;
        _ = ExportAnchors();
    }

    internal string TrustPath => Path.Combine(_directory, "trust.protected");

    internal string Issue(string name, IEnumerable<string> hosts)
    {
        lock (_gate)
        {
            CreateDirectory();
            using X509Certificate2 root = LoadOrCreateRoot();
            AddAnchor(root);
            string[] names = hosts.Concat(["localhost", "127.0.0.1", "::1"])
                .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains('/') || name.Contains('\\'))
            {
                throw new InvalidOperationException($"Certificate leaf name '{name}' is not a single path segment.");
            }
            string path = Path.Combine(_directory, name + ".pem.protected");
            if (File.Exists(path))
            {
                byte[] cached = new HostingMount(path).ReadAllBytes();
                try
                {
                    string pem = Encoding.UTF8.GetString(cached);
                    using X509Certificate2 leaf = X509Certificate2.CreateFromPem(pem, pem);
                    using var validation = new X509Chain();
                    validation.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                    validation.ChainPolicy.CustomTrustStore.Add(root);
                    validation.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                    validation.ChainPolicy.DisableCertificateDownloads = true;
                    X509SubjectAlternativeNameExtension? san = leaf.Extensions.OfType<X509SubjectAlternativeNameExtension>().FirstOrDefault();
                    string[] cachedNames = san is null ? [] : san.EnumerateDnsNames().Concat(san.EnumerateIPAddresses().Select(address => address.ToString())).Order(StringComparer.OrdinalIgnoreCase).ToArray();
                    if (leaf.NotAfter.ToUniversalTime() > DateTime.UtcNow.AddDays(1) && names.SequenceEqual(cachedNames, StringComparer.OrdinalIgnoreCase)
                        && validation.Build(leaf))
                    {
                        return pem;
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(cached);
                }
            }

            using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = new CertificateRequest(new X500DistinguishedName($"CN={name}"), key, HashAlgorithmName.SHA256);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));
            request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
            var alternatives = new SubjectAlternativeNameBuilder();
            foreach (string host in names)
            {
                if (IPAddress.TryParse(host, out IPAddress? address))
                {
                    alternatives.AddIpAddress(address);
                }
                else
                {
                    alternatives.AddDnsName(host);
                }
            }
            request.CertificateExtensions.Add(alternatives.Build());
            using X509Certificate2 issued = request.Create(root, DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(30), RandomNumberGenerator.GetBytes(16));
            string bundle = issued.ExportCertificatePem() + "\n" + key.ExportPkcs8PrivateKeyPem() + "\n";
            Write(path, Encoding.UTF8.GetBytes(bundle), protect: true);
            return bundle;
        }
    }

    internal void AddAnchors(string pem)
    {
        lock (_gate)
        {
            var certificates = new X509Certificate2Collection();
            try
            {
                certificates.ImportFromPem(pem);
                foreach (X509Certificate2 certificate in certificates)
                {
                    if (certificate.SubjectName.RawData.AsSpan().SequenceEqual(certificate.IssuerName.RawData))
                    {
                        AddAnchor(certificate);
                    }
                }
            }
            finally
            {
                foreach (X509Certificate2 certificate in certificates)
                {
                    certificate.Dispose();
                }
            }
        }
    }

    internal ReadOnlyMemory<byte> ExportAnchors()
    {
        lock (_gate)
        {
            if (_anchors.Count == 0 && File.Exists(TrustPath))
            {
                byte[] previous = new HostingMount(TrustPath).ReadAllBytes();
                try { AddAnchors(Encoding.UTF8.GetString(previous)); }
                finally { CryptographicOperations.ZeroMemory(previous); }
            }
            return Encoding.UTF8.GetBytes(string.Join("\n", _anchors.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Value)));
        }
    }

    private void AddAnchor(X509Certificate2 certificate)
    {
        _anchors[certificate.Thumbprint] = certificate.ExportCertificatePem();
        CreateDirectory();
        Write(TrustPath, Encoding.UTF8.GetBytes(string.Join("\n", _anchors.Values)), protect: true);
    }

    private X509Certificate2 LoadOrCreateRoot()
    {
        string certificatePath = Path.Combine(_directory, "root.crt");
        string keyPath = Path.Combine(_directory, "root.key.protected");
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        if (File.Exists(certificatePath) || File.Exists(keyPath))
        {
            byte[] encodedKey = new HostingMount(keyPath).ReadAllBytes();
            try { key.ImportPkcs8PrivateKey(encodedKey, out _); }
            finally { CryptographicOperations.ZeroMemory(encodedKey); }
            using X509Certificate2 certificate = X509Certificate2.CreateFromPem(File.ReadAllText(certificatePath));
            return certificate.CopyWithPrivateKey(key);
        }
        var request = new CertificateRequest(new X500DistinguishedName($"CN=Cohesion {_application} development root"), key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        X509Certificate2 root = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(5));
        Write(keyPath, key.ExportPkcs8PrivateKey(), protect: true);
        Write(certificatePath, Encoding.UTF8.GetBytes(root.ExportCertificatePem()), protect: false);
        return root;
    }

    private void CreateDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(_directory);
        }
        else
        {
            Directory.CreateDirectory(_directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private void Write(string path, byte[] bytes, bool protect)
    {
        byte[] stored = bytes;
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            if (protect && OperatingSystem.IsWindows())
            {
                stored = new WindowsLocalFileProtector(_directory, _application).Protect(_application.ToString(), Path.GetFileName(path), bytes);
            }

            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            using (var stream = new FileStream(temporary, options)) { stream.Write(stored); stream.Flush(flushToDisk: true); }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            if (!ReferenceEquals(bytes, stored))
            {
                CryptographicOperations.ZeroMemory(stored);
            }

            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}
