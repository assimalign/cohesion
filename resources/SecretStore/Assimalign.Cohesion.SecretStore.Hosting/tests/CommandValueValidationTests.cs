using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Hosting.Resources;
using Assimalign.Cohesion.SecretStore;
using Assimalign.Cohesion.SecretStore.Hosting.Internal;
using Assimalign.Cohesion.Security.DataProtection;

namespace Assimalign.Cohesion.SecretStore.Hosting.Tests;

public sealed class CommandValueValidationTests
{
    [Theory(DisplayName = "Cohesion Test [SecretStore.Hosting] - Delivered secret: malformed source is refused before persistence")]
    [InlineData("store:key:extra")]
    [InlineData("literal:value")]
    [InlineData("LITERAL:value")]
    [InlineData("store$:key")]
    public async Task ExecuteAsync_WithInvalidSource_ShouldRejectBeforeRepositoryAccess(string source)
    {
        // Arrange
        using var directory = new TemporaryDirectory();
        string storePath = Path.Combine(directory.Path, "store");
        IDataProtectionProvider protection = CreateProtectionProvider(directory.Path);
        SecretStoreRepository repository = CreateRepository(storePath, protection);
        using CertificateAuthorityManager authority = CreateAuthority(storePath, protection);
        var handler = new SecretStoreResourceCommandHandler(SecretStoreResourceCommandHandler.AddSecret, repository, authority);
        ResourceCommand command = CreateSecretCommand("app/key", source);

        // Act
        await Should.ThrowAsync<ResourceCommandRejectedException>(
            () => handler.ExecuteAsync(command, CancellationToken.None).AsTask());

        // Assert
        Directory.Exists(storePath).ShouldBeFalse();
    }

    [Theory(DisplayName = "Cohesion Test [SecretStore.Hosting] - Delivered secret: reserved paths remain refused before persistence")]
    [InlineData("trusted-issuers.json")]
    [InlineData("certs/api")]
    public async Task ExecuteAsync_WithReservedCommandPath_ShouldRejectBeforeRepositoryAccess(string path)
    {
        // Arrange
        using var directory = new TemporaryDirectory();
        string storePath = Path.Combine(directory.Path, "store");
        IDataProtectionProvider protection = CreateProtectionProvider(directory.Path);
        SecretStoreRepository repository = CreateRepository(storePath, protection);
        using CertificateAuthorityManager authority = CreateAuthority(storePath, protection);
        var handler = new SecretStoreResourceCommandHandler(SecretStoreResourceCommandHandler.AddSecret, repository, authority);
        ResourceCommand command = CreateSecretCommand(path, "parameter:key");

        // Act
        await Should.ThrowAsync<ResourceCommandRejectedException>(
            () => handler.ExecuteAsync(command, CancellationToken.None).AsTask());

        // Assert
        Directory.Exists(storePath).ShouldBeFalse();
    }

    [Theory(DisplayName = "Cohesion Test [SecretStore.Hosting] - Delivered certificate: invalid leaf name is refused before authority access")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("path/leaf")]
    [InlineData("path\\leaf")]
    public async Task ExecuteAsync_WithInvalidLeafName_ShouldRejectBeforeAuthorityAccess(string name)
    {
        // Arrange
        using var directory = new TemporaryDirectory();
        string storePath = Path.Combine(directory.Path, "store");
        IDataProtectionProvider protection = CreateProtectionProvider(directory.Path);
        SecretStoreRepository repository = CreateRepository(storePath, protection);
        using CertificateAuthorityManager authority = CreateAuthority(storePath, protection);
        var handler = new SecretStoreResourceCommandHandler(SecretStoreResourceCommandHandler.IssueCertificate, repository, authority);
        ResourceCommand command = CreateCertificateCommand(name, ["api.example.test"]);

        // Act
        await Should.ThrowAsync<ResourceCommandRejectedException>(
            () => handler.ExecuteAsync(command, CancellationToken.None).AsTask());

        // Assert
        Directory.Exists(storePath).ShouldBeFalse();
    }

    [Theory(DisplayName = "Cohesion Test [SecretStore.Hosting] - Delivered certificate: malformed alternative name is refused before authority access")]
    [InlineData("bad\0name", typeof(ResourceCommandRejectedException))]
    [InlineData("bad..name", typeof(ResourceCommandRejectedException))]
    [InlineData("", typeof(JsonException))]
    [InlineData(" \t", typeof(JsonException))]
    public async Task ExecuteAsync_WithInvalidAlternativeName_ShouldRejectBeforeAuthorityAccess(string name, Type expectedException)
    {
        // Arrange
        using var directory = new TemporaryDirectory();
        string storePath = Path.Combine(directory.Path, "store");
        IDataProtectionProvider protection = CreateProtectionProvider(directory.Path);
        SecretStoreRepository repository = CreateRepository(storePath, protection);
        using CertificateAuthorityManager authority = CreateAuthority(storePath, protection);
        var handler = new SecretStoreResourceCommandHandler(SecretStoreResourceCommandHandler.IssueCertificate, repository, authority);
        ResourceCommand command = CreateCertificateCommand("api", [name]);

        // Act
        Exception error = await Should.ThrowAsync<Exception>(
            () => handler.ExecuteAsync(command, CancellationToken.None).AsTask());

        // Assert
        error.GetType().ShouldBe(expectedException);
        Directory.Exists(storePath).ShouldBeFalse();
    }

    [Theory(DisplayName = "Cohesion Test [SecretStore.Hosting] - Repository: raw invalid seed paths are refused before persistence")]
    [InlineData("")]
    [InlineData(" \t")]
    [InlineData("trusted-issuers.json")]
    public void Constructor_WithInvalidRawSeedPath_ShouldRejectBeforePersistence(string path)
    {
        // Arrange
        using var directory = new TemporaryDirectory();
        string storePath = Path.Combine(directory.Path, "store");
        IDataProtectionProvider protection = CreateProtectionProvider(directory.Path);
        var seeds = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal)
        {
            [path] = new byte[] { 1, 2, 3 },
        };

        // Act
        ArgumentException error = Should.Throw<ArgumentException>(() => new SecretStoreRepository(
            storePath, protection.CreateProtector("tests"), seeds));

        // Assert
        error.ParamName.ShouldBe("seeds");
        Directory.Exists(storePath).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore.Hosting] - Delivered certificate: wildcard, IDN and IP alternative names issue successfully")]
    public async Task ExecuteAsync_WithWildcardIdnAndIpNames_ShouldPersistAcceptedCertificate()
    {
        // Arrange
        using var directory = new TemporaryDirectory();
        string storePath = Path.Combine(directory.Path, "store");
        IDataProtectionProvider protection = CreateProtectionProvider(directory.Path);
        SecretStoreRepository repository = CreateRepository(storePath, protection);
        using CertificateAuthorityManager authority = CreateAuthority(storePath, protection);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await authority.InitializeAsync(timeout.Token);
        var handler = new SecretStoreResourceCommandHandler(SecretStoreResourceCommandHandler.IssueCertificate, repository, authority);
        ResourceCommand command = CreateCertificateCommand("api", ["*.example.test", "bücher.example", "127.0.0.1", "::1"]);

        // Act
        ReadOnlyMemory<byte> result = await handler.ExecuteAsync(command, timeout.Token);
        string pem = await authority.GetCertificatePemAsync("certs/api", timeout.Token);

        // Assert
        result.IsEmpty.ShouldBeTrue();
        using X509Certificate2 certificate = X509Certificate2.CreateFromPem(pem);
        var names = new X509SubjectAlternativeNameExtension(certificate.Extensions["2.5.29.17"]!.RawData);
        names.EnumerateDnsNames().ShouldContain("*.example.test");
        names.EnumerateDnsNames().ShouldContain("xn--bcher-kva.example");
        names.EnumerateIPAddresses().ShouldContain(IPAddress.Loopback);
        names.EnumerateIPAddresses().ShouldContain(IPAddress.IPv6Loopback);
    }

    private static SecretStoreRepository CreateRepository(string storePath, IDataProtectionProvider protection) =>
        new(storePath, protection.CreateProtector("tests"), new Dictionary<string, ReadOnlyMemory<byte>>());

    private static CertificateAuthorityManager CreateAuthority(string storePath, IDataProtectionProvider protection) =>
        new(storePath, protection, new CertificateAuthorityOptions(), "test-app", "secrets");

    private static IDataProtectionProvider CreateProtectionProvider(string path) =>
        DataProtectionProvider.Create(FileSystemKeyRepository.Create(Path.Combine(path, "key-ring")));

    private static ResourceCommand CreateSecretCommand(string path, string source)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("path", path);
            writer.WriteString("source", source);
            writer.WriteString("resolvedValue", "AQID");
            writer.WriteEndObject();
        }

        return new ResourceCommand("secret-command", SecretStoreResourceCommandHandler.AddSecret, "test-app", path, buffer.ToArray());
    }

    private static ResourceCommand CreateCertificateCommand(string name, string[] alternatives)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("name", name);
            writer.WriteString("subject", "CN=api.example.test");
            writer.WriteStartArray("subjectAlternativeNames");
            foreach (string alternative in alternatives)
            {
                writer.WriteStringValue(alternative);
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return new ResourceCommand("certificate-command", SecretStoreResourceCommandHandler.IssueCertificate, "test-app", name, buffer.ToArray());
    }
}
