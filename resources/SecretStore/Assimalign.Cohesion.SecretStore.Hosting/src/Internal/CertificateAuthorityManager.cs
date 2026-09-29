using System;
using System.Buffers;
using System.Collections.Generic;
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

using Assimalign.Cohesion.Hosting.Resources;
using Assimalign.Cohesion.Security.DataProtection;

namespace Assimalign.Cohesion.SecretStore.Hosting.Internal;

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
    private readonly string _pendingPath;
    private readonly IDataProtector _authorityProtector;
    private readonly IDataProtector _leafProtector;
    private readonly CertificateAuthorityOptions _options;
    private readonly string? _applicationName;
    private readonly string? _resourceName;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private X509Certificate2? _issuer;
    private X509Certificate2[] _issuerChain = [];
    private bool _initialized;

    internal CertificateAuthorityManager(
        string dataPath,
        IDataProtectionProvider protectionProvider,
        CertificateAuthorityOptions options,
        string? applicationName,
        string? resourceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataPath);
        ArgumentNullException.ThrowIfNull(protectionProvider);
        ArgumentNullException.ThrowIfNull(options);

        string authorityDirectory = Path.Combine(Path.GetFullPath(dataPath), "certificates");
        _authorityPath = Path.Combine(authorityDirectory, "authority.protected");
        _pendingPath = Path.Combine(authorityDirectory, "enrollment.protected");
        _leafDirectoryPath = Path.Combine(authorityDirectory, "leaves");
        _authorityProtector = protectionProvider.CreateProtector("secret-store", "certificate-authority", "v1");
        _leafProtector = protectionProvider.CreateProtector("secret-store", "certificate-leaf", "v1");
        _options = options;
        _applicationName = applicationName;
        _resourceName = resourceName;
    }

    internal bool IsEnrolled => _issuer is not null;

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
                else if (_options.PlatformEnrollmentEndpoint is not null)
                {
                    if (string.IsNullOrWhiteSpace(_applicationName) ||
                        string.IsNullOrWhiteSpace(_resourceName))
                    {
                        throw new InvalidOperationException(
                            "Platform enrollment requires ambient application and resource names.");
                    }

                    byte[] pending = await EnsurePendingEnrollmentAsync(
                            _applicationName,
                            _resourceName,
                            cancellationToken)
                        .ConfigureAwait(false);
                    CryptographicOperations.ZeroMemory(pending);
                }
                else if (_options.SelfSeedWhenNoPlatform)
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
                        "The SecretStore has no durable certificate authority, initial authority material, Platform enrollment, or standalone self-seeding configured.");
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

    internal Task<string> GetCertificatePemAsync(
        string name,
        CancellationToken cancellationToken)
        => GetCertificatePemAsync(name, null, null, cancellationToken);

    internal async Task<string> GetCertificatePemAsync(
        string name,
        string? subject,
        IReadOnlyList<string>? subjectAlternativeNames,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureReady();
            if (string.Equals(name, "ca/root", StringComparison.Ordinal))
            {
                return ExportCertificatePem(RootCertificate);
            }

            string leafName = NormalizeLeafName(name);
            X500DistinguishedName? effectiveSubject = subject is null ? null :
                subject.Contains('=') ? new X500DistinguishedName(subject) : CreateSubject(subject);
            X509Extension? effectiveNames = subjectAlternativeNames is null ? null : CreateAlternativeNames(subjectAlternativeNames);
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
                    X509Extension? storedNames = existing.Extensions["2.5.29.17"];
                    if ((effectiveSubject is not null && !effectiveSubject.RawData.AsSpan().SequenceEqual(existing.SubjectName.RawData)) ||
                        (effectiveNames is not null && (storedNames is null || !effectiveNames.RawData.AsSpan().SequenceEqual(storedNames.RawData))))
                    {
                        throw new Assimalign.Cohesion.Hosting.Resources.ResourceCommandRejectedException(
                            $"secretstore.issue-certificate certificate '{leafName}' already has a different subject or SAN set; delete its declaration before changing its identity.");
                    }
                    effectiveSubject ??= existing.SubjectName;
                    effectiveNames ??= storedNames;
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
                subjectAlternativeNames ?? [leafName, "localhost", "127.0.0.1", "::1"], effectiveSubject, effectiveNames);
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

    internal async Task<string> CreateEnrollmentRequestAsync(
        string applicationName,
        string resourceName,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationName);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceName);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_issuer is not null)
            {
                throw new InvalidOperationException("The SecretStore certificate authority is already initialized.");
            }

            byte[] pending = await EnsurePendingEnrollmentAsync(
                    applicationName,
                    resourceName,
                    cancellationToken)
                .ConfigureAwait(false);
            try
            {
                using JsonDocument document = JsonDocument.Parse(pending);
                JsonElement root = document.RootElement;
                if (!string.Equals(
                        root.GetProperty("application").GetString(),
                        applicationName,
                        StringComparison.Ordinal) ||
                    !string.Equals(
                        root.GetProperty("resource").GetString(),
                        resourceName,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "The pending enrollment belongs to a different application resource.");
                }

                return document.RootElement.GetProperty("certificateSigningRequest").GetString()!;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(pending);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task CompleteEnrollmentAsync(
        string certificatePem,
        IReadOnlyList<string> issuerChainPem,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(certificatePem);
        ArgumentNullException.ThrowIfNull(issuerChainPem);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_issuer is not null)
            {
                throw new InvalidOperationException("The SecretStore certificate authority is already initialized.");
            }

            byte[] pending = await ProtectedFileStore.ReadAsync(
                    _pendingPath,
                    _authorityProtector,
                    cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException("No pending SecretStore enrollment exists.");
            try
            {
                using JsonDocument document = JsonDocument.Parse(pending);
                string applicationName = GetRequiredPendingString(
                    document.RootElement,
                    "application");
                string resourceName = GetRequiredPendingString(
                    document.RootElement,
                    "resource");
                string privateKeyPem = GetRequiredPendingString(
                    document.RootElement,
                    "privateKey");
                X509Certificate2 enrolled = LoadCertificateWithKey(certificatePem, privateKeyPem);
                X509Certificate2[] chain = LoadCertificates(issuerChainPem);
                try
                {
                    ValidateAuthority(enrolled);
                    X500DistinguishedName expectedSubject = CreateSubject(
                        applicationName + "/" + resourceName);
                    if (!enrolled.SubjectName.RawData.AsSpan().SequenceEqual(expectedSubject.RawData))
                    {
                        throw new InvalidDataException(
                            "The enrolled authority subject does not match its pending application resource.");
                    }

                    ValidatePlatformPin(chain);
                    ValidateIssuedChain(enrolled, chain);
                    await PersistAuthorityAsync(enrolled, chain, cancellationToken)
                        .ConfigureAwait(false);
                    _issuer = enrolled;
                    _issuerChain = chain;
                }
                catch
                {
                    enrolled.Dispose();
                    DisposeCertificates(chain);
                    throw;
                }

                File.Delete(_pendingPath);
                _initialized = true;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(pending);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task<CertificateEnrollmentResponse> IssueIntermediateAsync(
        string applicationName,
        string resourceName,
        string certificateSigningRequest,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationName);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(certificateSigningRequest);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureReady();
            CertificateRequest incoming = CertificateRequest.LoadSigningRequestPem(
                certificateSigningRequest,
                HashAlgorithmName.SHA256,
                CertificateRequestLoadOptions.Default,
                null);
            X500DistinguishedName expectedSubject = CreateSubject(applicationName + "/" + resourceName);
            if (!incoming.SubjectName.RawData.AsSpan().SequenceEqual(expectedSubject.RawData))
            {
                throw new InvalidDataException(
                    "The enrollment request subject does not match its application and resource identity.");
            }

            var request = new CertificateRequest(
                incoming.SubjectName,
                incoming.PublicKey,
                HashAlgorithmName.SHA256);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(
                X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign,
                true));
            request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

            byte[] serial = CreateSerialNumber();
            try
            {
                using X509Certificate2 certificate = request.Create(
                    _issuer!,
                    DateTimeOffset.UtcNow.AddMinutes(-5),
                    ClampNotAfter(_issuer!, DateTimeOffset.UtcNow.AddYears(5)),
                    serial);
                var chain = new List<string>(_issuerChain.Length + 1)
                {
                    ExportCertificatePem(_issuer!),
                };
                for (int index = 0; index < _issuerChain.Length; index++)
                {
                    chain.Add(ExportCertificatePem(_issuerChain[index]));
                }

                return new CertificateEnrollmentResponse(
                    ExportCertificatePem(certificate),
                    chain);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(serial);
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

    private async Task<byte[]> EnsurePendingEnrollmentAsync(
        string applicationName,
        string resourceName,
        CancellationToken cancellationToken)
    {
        byte[]? durable = await ProtectedFileStore.ReadAsync(
                _pendingPath,
                _authorityProtector,
                cancellationToken)
            .ConfigureAwait(false);
        if (durable is not null)
        {
            return durable;
        }

        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(
            CreateSubject(applicationName + "/" + resourceName),
            key,
            HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign,
            true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

        string privateKeyPem = key.ExportPkcs8PrivateKeyPem();
        string csrPem = request.CreateSigningRequestPem();
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("application", applicationName);
            writer.WriteString("resource", resourceName);
            writer.WriteString("certificateSigningRequest", csrPem);
            writer.WriteString("privateKey", privateKeyPem);
            writer.WriteEndObject();
        }

        byte[] payload = buffer.WrittenSpan.ToArray();
        buffer.Clear();
        await ProtectedFileStore.WriteAsync(
                _pendingPath,
                _authorityProtector,
                payload,
                cancellationToken)
            .ConfigureAwait(false);
        return payload;
    }

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

    internal async Task DeleteCertificateAsync(string name, CancellationToken cancellationToken = default)
    {
        string leafName = NormalizeLeafName("certs/" + name);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureReady();
            File.Delete(GetLeafPath(leafName));
        }
        finally
        {
            _gate.Release();
        }
    }

    private static X509Extension CreateAlternativeNames(IReadOnlyList<string> values)
    {
        var names = new SubjectAlternativeNameBuilder();
        foreach (string value in values.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal))
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
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

    private static X509Certificate2[] LoadCertificates(IReadOnlyList<string> certificatePem)
    {
        var certificates = new X509Certificate2[certificatePem.Count];
        try
        {
            for (int index = 0; index < certificates.Length; index++)
            {
                certificates[index] = X509Certificate2.CreateFromPem(certificatePem[index]);
            }

            return certificates;
        }
        catch
        {
            DisposeCertificates(certificates);
            throw;
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
            throw new InvalidDataException("The enrolled certificate does not chain to its supplied root.");
        }
    }

    private void ValidatePlatformPin(IReadOnlyList<X509Certificate2> issuerChain)
    {
        if (!_options.PlatformCertificate.HasValue)
        {
            return;
        }

        ReadOnlyMemory<byte> pinnedBytes = _options.PlatformCertificate.Value;
        if (issuerChain.Count == 0)
        {
            throw new InvalidDataException(
                "A Platform-enrolled authority requires the pinned Platform issuer chain.");
        }

        string pinnedPem = Encoding.UTF8.GetString(pinnedBytes.Span);
        using X509Certificate2 pinned = X509Certificate2.CreateFromPem(pinnedPem);
        if (!CryptographicOperations.FixedTimeEquals(
                pinned.RawData,
                issuerChain[^1].RawData))
        {
            throw new InvalidDataException(
                "The enrollment response does not terminate at the configured Platform certificate.");
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

    private IReadOnlyList<string> GetServerSubjectAlternativeNames(
        string resourceName,
        string endpointHost)
    {
        var names = new List<string>
        {
            endpointHost,
            resourceName,
            resourceName + "-api",
        };
        if (!string.IsNullOrWhiteSpace(_applicationName))
        {
            names.Add(resourceName + "." + _applicationName + ".svc");
            names.Add(resourceName + "." + _applicationName + ".svc.cluster.local");
            names.Add(resourceName + "-api." + _applicationName + ".svc");
            names.Add(resourceName + "-api." + _applicationName + ".svc.cluster.local");
        }

        return names;
    }

    private static string GetRequiredPendingString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement property) ||
            property.ValueKind is not JsonValueKind.String ||
            string.IsNullOrWhiteSpace(property.GetString()))
        {
            throw new InvalidDataException(
                $"The pending enrollment requires string member '{name}'.");
        }

        return property.GetString()!;
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
        if (!name.StartsWith(CertificatePrefix, StringComparison.Ordinal) ||
            name.Length == CertificatePrefix.Length)
        {
            throw new ArgumentException(
                "Certificate names must use the 'certs/<name>' namespace.",
                nameof(name));
        }

        string leafName = name[CertificatePrefix.Length..];
        if (string.Equals(leafName, PublicCertificateName, StringComparison.Ordinal))
        {
            throw new NotSupportedException(
                "Certificate='public' uses ACME or another public CA and is not implemented by this design item.");
        }

        if (leafName.Contains('/', StringComparison.Ordinal) ||
            leafName.Contains('\\', StringComparison.Ordinal) ||
            leafName is "." or "..")
        {
            throw new ArgumentException("A certificate leaf name must be one path segment.", nameof(name));
        }

        return leafName;
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

        if (_issuer is null)
        {
            throw new InvalidOperationException(
                "The certificate authority is awaiting gateway-mediated Platform enrollment.");
        }
    }
}

internal sealed record CertificateEnrollmentResponse(
    string Certificate,
    IReadOnlyList<string> IssuerChain);
