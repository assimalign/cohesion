using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using ConfigurationClientCredential = Assimalign.Cohesion.ConfigurationStore.Client.ClientCredential;
using ConfigurationStoreProtocolClient = Assimalign.Cohesion.ConfigurationStore.Client.ConfigurationStoreClient;
using SecretClientCredential = Assimalign.Cohesion.SecretStore.Client.ClientCredential;
using SecretStoreProtocolClient = Assimalign.Cohesion.SecretStore.Client.SecretStoreClient;
using SecretStoreResourceCommand = Assimalign.Cohesion.SecretStore.Client.ResourceCommand;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

internal sealed class GatewayStoreClient : IGatewayStoreClient
{
    public static GatewayStoreClient Instance { get; } = new();

    private readonly ConcurrentDictionary<string, RemoteCertificateValidationCallback> _transportTrust = new(StringComparer.Ordinal);

    internal GatewayStoreClient()
    {
    }

    internal void SetTransportTrust(Uri endpoint, RemoteCertificateValidationCallback? validator)
    {
        if (validator is not null)
        {
            _transportTrust[endpoint.GetLeftPart(UriPartial.Authority)] = validator;
        }
    }

    private HttpMessageInvoker CreateTransport(Uri endpoint)
    {
        _transportTrust.TryGetValue(endpoint.GetLeftPart(UriPartial.Authority), out RemoteCertificateValidationCallback? validator);
        return GatewayHttpTransport.Create(validator);
    }

    public async ValueTask<ReadOnlyMemory<byte>> ReadSecretAsync(
        Uri endpoint,
        string credential,
        string path,
        CancellationToken cancellationToken = default)
    {
        using HttpMessageInvoker transport = CreateTransport(endpoint);
        return await SecretStoreProtocolClient
            .Create(endpoint, new SecretClientCredential(credential), transport)
            .GetSecretAsync(path, cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<string> ReadCertificateAsync(
        Uri endpoint,
        string credential,
        string name,
        CancellationToken cancellationToken = default)
    {
        using HttpMessageInvoker transport = CreateTransport(endpoint);
        return await SecretStoreProtocolClient
            .Create(endpoint, new SecretClientCredential(credential), transport)
            .GetCertificateAsync(name, cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyDictionary<string, string?>> ReadConfigurationAsync(
        Uri endpoint,
        string credential,
        string name,
        CancellationToken cancellationToken = default)
    {
        using HttpMessageInvoker transport = CreateTransport(endpoint);
        return await ConfigurationStoreProtocolClient
            .Create(endpoint, new ConfigurationClientCredential(credential), transport)
            .GetNamespaceAsync(name, cancellationToken)
            .ConfigureAwait(false);
    }

    public ValueTask StoreTrustedIssuerAsync(
        Uri endpoint,
        string credential,
        string owner,
        string issuer,
        ReadOnlyMemory<byte> publicKey,
        CancellationToken cancellationToken = default)
        => StoreTrustedIssuerAsync(endpoint, credential, owner, issuer, publicKey, null, cancellationToken);

    public async ValueTask StoreTrustedIssuerAsync(Uri endpoint, string credential, string owner, string issuer,
        ReadOnlyMemory<byte> publicKey, IReadOnlyList<string>? allowedCommandKinds, CancellationToken cancellationToken = default)
    {
        ReadOnlyMemory<byte> payload = publicKey;
        if (allowedCommandKinds is { Count: > 0 })
        {
            using JsonDocument document = JsonDocument.Parse(publicKey);
            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WritePropertyName("trustKey");
                document.RootElement.WriteTo(writer);
                writer.WriteStartArray("allowedCommandKinds");
                foreach (string kind in allowedCommandKinds) { writer.WriteStringValue(kind); }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            payload = buffer.WrittenMemory.ToArray();
        }
        byte[] identity = Encoding.UTF8.GetBytes(owner + "\n" + issuer + "\n");
        byte[] commandHash;
        try
        {
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            hasher.AppendData(identity);
            hasher.AppendData(payload.Span);
            commandHash = hasher.GetHashAndReset();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(identity);
        }

        var command = new SecretStoreResourceCommand(
            "trust-" + Convert.ToHexStringLower(commandHash),
            "cohesion.trust.add",
            owner,
            issuer,
            payload);
        try
        {
            using HttpMessageInvoker transport = CreateTransport(endpoint);
            await SecretStoreProtocolClient
                .Create(endpoint, new SecretClientCredential(credential), transport)
                .SendCommandAsync(command, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(commandHash);
        }
    }
}
