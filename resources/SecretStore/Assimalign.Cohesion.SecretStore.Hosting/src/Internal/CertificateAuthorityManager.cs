using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Security.DataProtection;

using Assimalign.Cohesion.SecretStore.Client.Internal;

namespace Assimalign.Cohesion.SecretStore.Hosting.Internal;

[global::System.Diagnostics.DebuggerDisplay("CertificateAuthorityManager")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed class CertificateAuthorityManager : IDisposable
{
    private const string CertificatePrefix = "certs/";
    private const string PublicCertificateName = "public";
    private static readonly TimeSpan _leafRenewalWindow = TimeSpan.FromDays(7);

    // macOS cannot load PKCS#12 private keys as ephemeral (X509CertificateLoader rejects EphemeralKeySet with
    // PlatformNotSupportedException); it stores them in a temporary keychain instead. Every other platform keeps
    // the keys out of the persistent store.
    private static readonly X509KeyStorageFlags _privateKeyStorageFlags = OperatingSystem.IsMacOS()
        ? X509KeyStorageFlags.Exportable
        : X509KeyStorageFlags.EphemeralKeySet | X509KeyStorageFlags.Exportable;

    private readonly string _authorityPath;
    private readonly string _leafDirectoryPath;
    private readonly IDataProtector _authorityProtector;
    private readonly IDataProtector _leafProtector;
    private readonly CertificateAuthorityOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private X509Certificate2? _issuer;
    private X509Certificate2[] _issuerChain = [];
    private bool _initialized;

    internal CertificateAuthorityManager(
        string dataPath,
        IDataProtectionProvider protectionProvider,
        CertificateAuthorityOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataPath);
        ArgumentNullException.ThrowIfNull(protectionProvider);
        ArgumentNullException.ThrowIfNull(options);

        string authorityDirectory = Path.Combine(Path.GetFullPath(dataPath), "certificates");
        _authorityPath = Path.Combine(authorityDirectory, "authority.protected");
        _leafDirectoryPath = Path.Combine(authorityDirectory, "leaves");
        _authorityProtector = protectionProvider.CreateProtector("secret-store", "certificate-authority", "v1");
        _leafProtector = protectionProvider.CreateProtector("secret-store", "certificate-leaf", "v1");
        _options = options;
    }

    internal async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return;
            }

            byte[]? durable = await ProtectedFileStore.ReadAsync(
                    _authorityPath,
                    _authorityProtector,
                    cancellationToken)
                .ConfigureAwait(false);
            try
            {
                if (durable is not null)
                {
                    LoadAuthority(durable);
                }
                else if (_options.InitialCertificate.HasValue &&
                    _options.InitialPrivateKey.HasValue)
                {
                    ReadOnlyMemory<byte> certificate = _options.InitialCertificate.Value;
                    ReadOnlyMemory<byte> privateKey = _options.InitialPrivateKey.Value;
                    X509Certificate2 initial = LoadInitialAuthority(
                        certificate.Span,
                        privateKey.Span);
                    try
                    {
                        await PersistAuthorityAsync(initial, [], cancellationToken)
                            .ConfigureAwait(false);
                        _issuer = initial;
                        _issuerChain = [];
                    }
                    catch
                    {
                        initial.Dispose();
                        throw;
                    }
                }
                else if (_options.SelfSeed)
                {
                    X509Certificate2 root = CreateRoot(_options.CommonName);
                    try
                    {
                        await PersistAuthorityAsync(root, [], cancellationToken)
                            .ConfigureAwait(false);
                        _issuer = root;
                        _issuerChain = [];
                    }
                    catch
                    {
                        root.Dispose();
                        throw;
                    }
                }
                else
                {
                    throw new InvalidOperationException(
                        "The SecretStore has no durable certificate authority, initial authority material, or self-seeding configured.");
                }
            }
            finally
            {
                if (durable is not null)
                {
                    CryptographicOperations.ZeroMemory(durable);
                }
            }

            _initialized = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task<string> GetCertificatePemAsync(
        string name,
        CancellationToken cancellationToken)
    {
        if (!SecretStoreCertificateReferenceGrammar.IsValid(name))
        {
            throw new ArgumentException(ErrorMessages.CertificateReferenceInvalid, nameof(name));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureReady();
            if (string.Equals(name, "ca/root", StringComparison.Ordinal))
            {
                return ExportCertificatePem(RootCertificate);
            }

            string leafName = NormalizeLeafName(name);
            X500DistinguishedName? effectiveSubject = null;
            X509Extension? effectiveNames = null;
            string leafPath = GetLeafPath(leafName);
            byte[]? durable = await ProtectedFileStore.ReadAsync(
                    leafPath,
                    _leafProtector,
                    cancellationToken)
                .ConfigureAwait(false);
            if (durable is not null)
            {
                try
                {
                    string existingPem = Encoding.UTF8.GetString(durable);
                    using X509Certificate2 existing = LoadLeafBundle(existingPem);

                    // A renewal keeps the subject and alternative names the leaf was issued with.
                    effectiveSubject = existing.SubjectName;
                    effectiveNames = existing.Extensions["2.5.29.17"];
                    if (!RequiresRenewal(existing, DateTimeOffset.UtcNow))
                    {
                        return existingPem;
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(durable);
                }
            }

            using X509Certificate2 leaf = CreateLeaf(leafName,
                [leafName, "localhost", "127.0.0.1", "::1"], effectiveSubject, effectiveNames);
            string pem = ExportLeafBundle(leaf);
            byte[] encoded = Encoding.UTF8.GetBytes(pem);
            try
            {
                await ProtectedFileStore.WriteAsync(
                        leafPath,
                        _leafProtector,
                        encoded,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(encoded);
            }

            return pem;
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task<SslStreamCertificateContext> GetServerCertificateContextAsync(
        string resourceName,
        string endpointHost,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointHost);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureReady();
            string leafName = resourceName + "-api";
            string leafPath = GetLeafPath("transport:" + resourceName + ":" + endpointHost);
            byte[]? durable = await ProtectedFileStore.ReadAsync(
                    leafPath,
                    _leafProtector,
                    cancellationToken)
                .ConfigureAwait(false);
            if (durable is null)
            {
                using X509Certificate2 created = CreateLeaf(
                    leafName,
                    GetServerSubjectAlternativeNames(resourceName, endpointHost));
                string pem = ExportLeafBundle(created);
                byte[] encoded = Encoding.UTF8.GetBytes(pem);
                try
                {
                    await ProtectedFileStore.WriteAsync(
                            leafPath,
                            _leafProtector,
                            encoded,
                            cancellationToken)
                        .ConfigureAwait(false);
                    return CreateServerCertificateContext(LoadLeafBundle(pem));
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(encoded);
                }
            }

            try
            {
                X509Certificate2 existing = LoadLeafBundle(Encoding.UTF8.GetString(durable));
                if (!RequiresRenewal(existing, DateTimeOffset.UtcNow))
                {
                    return CreateServerCertificateContext(existing);
                }

                existing.Dispose();
            }
            finally
            {
                CryptographicOperations.ZeroMemory(durable);
            }

            using X509Certificate2 renewed = CreateLeaf(
                leafName,
                GetServerSubjectAlternativeNames(resourceName, endpointHost));
            string renewedPem = ExportLeafBundle(renewed);
            byte[] renewedBytes = Encoding.UTF8.GetBytes(renewedPem);
            try
            {
                await ProtectedFileStore.WriteAsync(
                        leafPath,
                        _leafProtector,
                        renewedBytes,
                        cancellationToken)
                    .ConfigureAwait(false);
                return CreateServerCertificateContext(LoadLeafBundle(renewedPem));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(renewedBytes);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _issuer?.Dispose();
        DisposeCertificates(_issuerChain);
        _gate.Dispose();
    }

    private X509Certificate2 RootCertificate =>
        _issuerChain.Length == 0 ? _issuer! : _issuerChain[^1];

    private static X509Certificate2 LoadInitialAuthority(
        ReadOnlySpan<byte> certificate,
        ReadOnlySpan<byte> privateKey)
    {
        string certificatePem = Encoding.UTF8.GetString(certificate);
        string privateKeyPem = Encoding.UTF8.GetString(privateKey);
        X509Certificate2 loaded = LoadCertificateWithKey(certificatePem, privateKeyPem);
        try
        {
            ValidateAuthority(loaded);
            return loaded;
        }
        catch
        {
            loaded.Dispose();
            throw;
        }
    }

    private void LoadAuthority(ReadOnlyMemory<byte> payload)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(payload);
            JsonElement root = document.RootElement;
            byte[] pfx = root.GetProperty("authority").GetBytesFromBase64();
            try
            {
                X509Certificate2 issuer = X509CertificateLoader.LoadPkcs12(
                    pfx,
                    null,
                    _privateKeyStorageFlags);
                X509Certificate2[] chain = root.TryGetProperty("chain", out JsonElement chainProperty)
                    ? chainProperty.EnumerateArray()
                        .Select(static item => X509CertificateLoader.LoadCertificate(item.GetBytesFromBase64()))
                        .ToArray()
                    : [];
                try
                {
                    ValidateAuthority(issuer);
                    ValidateIssuedChain(issuer, chain);
                    _issuer = issuer;
                    _issuerChain = chain;
                }
                catch
                {
                    issuer.Dispose();
                    DisposeCertificates(chain);
                    throw;
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(pfx);
            }
        }
        catch (Exception exception) when (
            exception is JsonException or InvalidOperationException or CryptographicException)
        {
            throw new InvalidDataException("The protected certificate-authority state is invalid.", exception);
        }
    }

    private async Task PersistAuthorityAsync(
        X509Certificate2 issuer,
        IReadOnlyList<X509Certificate2> issuerChain,
        CancellationToken cancellationToken)
    {
        byte[] pfx = issuer.Export(X509ContentType.Pkcs12);
        try
        {
            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WriteBase64String("authority", pfx);
                writer.WritePropertyName("chain");
                writer.WriteStartArray();
                for (int index = 0; index < issuerChain.Count; index++)
                {
                    writer.WriteBase64StringValue(issuerChain[index].RawData);
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            try
            {
                await ProtectedFileStore.WriteAsync(
                        _authorityPath,
                        _authorityProtector,
                        buffer.WrittenMemory,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                buffer.Clear();
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pfx);
        }
    }

    private X509Certificate2 CreateLeaf(
        string name,
        IReadOnlyList<string> subjectAlternativeNames,
        X500DistinguishedName? subject = null,
        X509Extension? alternativeNames = null)
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(
            subject ?? CreateSubject(name),
            key,
            HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature,
            true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new("1.3.6.1.5.5.7.3.1") },
            false));
        request.CertificateExtensions.Add(alternativeNames ?? CreateAlternativeNames(subjectAlternativeNames));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

        byte[] serial = CreateSerialNumber();
        try
        {
            using X509Certificate2 publicCertificate = request.Create(
                _issuer!,
                DateTimeOffset.UtcNow.AddMinutes(-5),
                ClampNotAfter(_issuer!, DateTimeOffset.UtcNow.AddDays(90)),
                serial);
            using X509Certificate2 withKey = publicCertificate.CopyWithPrivateKey(key);
            return X509CertificateLoader.LoadPkcs12(
                withKey.Export(X509ContentType.Pkcs12),
                null,
                _privateKeyStorageFlags);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(serial);
        }
    }

    private static X509Extension CreateAlternativeNames(IReadOnlyList<string> values)
    {
        var names = new SubjectAlternativeNameBuilder();
        foreach (string value in values.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal))
        {
            if (!IsValidSubjectAlternativeName(value))
            {
                throw new ArgumentException(ErrorMessages.AlternativeNameInvalid, nameof(values));
            }
            if (IPAddress.TryParse(value, out IPAddress? address)) { names.AddIpAddress(address); }
            else { names.AddDnsName(value); }
        }
        return names.Build();
    }

    private string ExportLeafBundle(X509Certificate2 leaf)
    {
        using ECDsa key = leaf.GetECDsaPrivateKey()
            ?? throw new InvalidOperationException("The issued certificate has no ECDSA private key.");
        var builder = new StringBuilder();
        builder.Append(ExportCertificatePem(leaf));
        builder.Append('\n');
        builder.Append(key.ExportPkcs8PrivateKeyPem());
        builder.Append('\n');
        builder.Append(ExportCertificatePem(_issuer!));
        for (int index = 0; index < _issuerChain.Length; index++)
        {
            builder.Append('\n');
            builder.Append(ExportCertificatePem(_issuerChain[index]));
        }

        return builder.ToString();
    }

    private SslStreamCertificateContext CreateServerCertificateContext(X509Certificate2 leaf)
    {
        var additionalCertificates = new X509Certificate2Collection
        {
            _issuer!,
        };
        for (int index = 0; index < _issuerChain.Length; index++)
        {
            additionalCertificates.Add(_issuerChain[index]);
        }

        return SslStreamCertificateContext.Create(
            leaf,
            additionalCertificates,
            offline: true);
    }

    private static X509Certificate2 LoadLeafBundle(string pem)
    {
        X509Certificate2 loaded = X509Certificate2.CreateFromPem(pem, pem);
        try
        {
            byte[] pfx = loaded.Export(X509ContentType.Pkcs12);
            try
            {
                return X509CertificateLoader.LoadPkcs12(
                    pfx,
                    null,
                    _privateKeyStorageFlags);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(pfx);
            }
        }
        finally
        {
            loaded.Dispose();
        }
    }

    private static X509Certificate2 LoadCertificateWithKey(string certificatePem, string privateKeyPem)
    {
        X509Certificate2 loaded = X509Certificate2.CreateFromPem(certificatePem, privateKeyPem);
        try
        {
            byte[] pfx = loaded.Export(X509ContentType.Pkcs12);
            try
            {
                return X509CertificateLoader.LoadPkcs12(
                    pfx,
                    null,
                    _privateKeyStorageFlags);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(pfx);
            }
        }
        finally
        {
            loaded.Dispose();
        }
    }

    private static X509Certificate2 CreateRoot(string commonName)
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(
            CreateSubject(commonName),
            key,
            HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 1, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign,
            true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        using X509Certificate2 ephemeral = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddYears(10));
        byte[] pfx = ephemeral.Export(X509ContentType.Pkcs12);
        try
        {
            return X509CertificateLoader.LoadPkcs12(
                pfx,
                null,
                _privateKeyStorageFlags);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pfx);
        }
    }

    private static void ValidateAuthority(X509Certificate2 certificate)
    {
        using ECDsa? privateKey = certificate.GetECDsaPrivateKey();
        if (!certificate.HasPrivateKey || privateKey is null)
        {
            throw new InvalidDataException("A SecretStore authority must contain an ECDSA private key.");
        }

        X509BasicConstraintsExtension? constraints = certificate.Extensions
            .OfType<X509BasicConstraintsExtension>()
            .SingleOrDefault();
        if (constraints is null || !constraints.CertificateAuthority)
        {
            throw new InvalidDataException("A SecretStore authority certificate must be a certificate authority.");
        }

        X509KeyUsageExtension? keyUsage = certificate.Extensions
            .OfType<X509KeyUsageExtension>()
            .SingleOrDefault();
        if (keyUsage is null ||
            (keyUsage.KeyUsages & X509KeyUsageFlags.KeyCertSign) == 0)
        {
            throw new InvalidDataException(
                "A SecretStore authority certificate must permit certificate signing.");
        }
    }

    private static void ValidateIssuedChain(
        X509Certificate2 certificate,
        IReadOnlyList<X509Certificate2> issuerChain)
    {
        if (issuerChain.Count == 0)
        {
            if (!string.Equals(certificate.Subject, certificate.Issuer, StringComparison.Ordinal))
            {
                throw new InvalidDataException("A non-self-signed authority requires an issuer chain.");
            }
        }

        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.DisableCertificateDownloads = true;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
        chain.ChainPolicy.CustomTrustStore.Add(
            issuerChain.Count == 0 ? certificate : issuerChain[^1]);
        for (int index = 0; index < Math.Max(0, issuerChain.Count - 1); index++)
        {
            chain.ChainPolicy.ExtraStore.Add(issuerChain[index]);
        }

        if (!chain.Build(certificate))
        {
            throw new InvalidDataException("The authority certificate does not chain to its supplied root.");
        }
    }

    private static DateTimeOffset ClampNotAfter(
        X509Certificate2 issuer,
        DateTimeOffset requested)
    {
        DateTimeOffset issuerLimit = issuer.NotAfter.ToUniversalTime().AddMinutes(-1);
        DateTimeOffset result = requested < issuerLimit ? requested : issuerLimit;
        if (result <= DateTimeOffset.UtcNow)
        {
            throw new InvalidOperationException(
                "The certificate issuer expires too soon to issue another certificate.");
        }

        return result;
    }

    private static bool RequiresRenewal(X509Certificate2 certificate, DateTimeOffset now)
        => certificate.NotAfter.ToUniversalTime() <= now + _leafRenewalWindow;

    private static X500DistinguishedName CreateSubject(string commonName)
    {
        var builder = new X500DistinguishedNameBuilder();
        builder.AddCommonName(commonName);
        return builder.Build();
    }

    private static IReadOnlyList<string> GetServerSubjectAlternativeNames(
        string resourceName,
        string endpointHost)
    {
        return
        [
            endpointHost,
            resourceName,
            resourceName + "-api",
        ];
    }

    private string GetLeafPath(string leafName)
    {
        byte[] nameBytes = Encoding.UTF8.GetBytes(leafName);
        byte[] hash = SHA256.HashData(nameBytes);
        try
        {
            return Path.Combine(_leafDirectoryPath, Convert.ToHexStringLower(hash) + ".protected");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(nameBytes);
            CryptographicOperations.ZeroMemory(hash);
        }
    }

    private static string NormalizeLeafName(string name)
    {
        if (!SecretStoreCertificateReferenceGrammar.IsValid(name) ||
            !name.StartsWith(CertificatePrefix, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                ErrorMessages.CertificateReferenceInvalid,
                nameof(name));
        }

        string leafName = name[CertificatePrefix.Length..];
        if (string.Equals(leafName, PublicCertificateName, StringComparison.Ordinal))
        {
            throw new NotSupportedException(
                "Certificate='public' uses ACME or another public CA and is not implemented by this design item.");
        }

        if (!IsValidCertificateName(leafName))
        {
            throw new ArgumentException(ErrorMessages.CertificateNameInvalid, nameof(name));
        }

        return leafName;
    }

    // A leaf name is one path segment; an alternative name is an IP address or an IDN-encodable
    // DNS name.
    private static bool IsValidCertificateName(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value is not "." and not ".." &&
        value.IndexOfAny(['/', '\\']) < 0;

    private static bool IsValidSubjectAlternativeName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (IPAddress.TryParse(value, out _))
        {
            return true;
        }

        try
        {
            // SubjectAlternativeNameBuilder.AddDnsName uses default IdnMapping.GetAscii
            // before IA5 encoding. Parse that grammar directly; do not construct a certificate.
            // This preserves certificate wildcard/IDN acceptance rather than hostname rules.
            string ascii = new IdnMapping().GetAscii(value);
            return ascii.AsSpan().IndexOfAnyExceptInRange((char)0, (char)127) < 0;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static byte[] CreateSerialNumber()
    {
        byte[] serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7F;
        if (serial.All(static value => value == 0))
        {
            serial[^1] = 1;
        }

        return serial;
    }

    private static string ExportCertificatePem(X509Certificate2 certificate)
        => PemEncoding.WriteString("CERTIFICATE", certificate.RawData);

    private static void DisposeCertificates(IEnumerable<X509Certificate2?> certificates)
    {
        foreach (X509Certificate2? certificate in certificates)
        {
            certificate?.Dispose();
        }
    }

    private void EnsureReady()
    {
        if (!_initialized)
        {
            throw new InvalidOperationException("The certificate authority has not been initialized.");
        }
    }

    private sealed class DebugView
    {
        public DebugView(CertificateAuthorityManager value)
        {
        }
    }
}
