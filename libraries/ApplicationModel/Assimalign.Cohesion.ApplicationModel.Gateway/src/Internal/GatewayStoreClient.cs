using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
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

    private GatewayStoreClient()
    {
    }

    public async ValueTask<ReadOnlyMemory<byte>> ReadSecretAsync(
        Uri endpoint,
        string credential,
        string path,
        CancellationToken cancellationToken = default)
    {
        return await SecretStoreProtocolClient
            .Create(endpoint, new SecretClientCredential(credential))
            .GetSecretAsync(path, cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<string> ReadCertificateAsync(
        Uri endpoint,
        string credential,
        string name,
        CancellationToken cancellationToken = default)
    {
        return await SecretStoreProtocolClient
            .Create(endpoint, new SecretClientCredential(credential))
            .GetCertificateAsync(name, cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyDictionary<string, string?>> ReadConfigurationAsync(
        Uri endpoint,
        string credential,
        string name,
        CancellationToken cancellationToken = default)
    {
        return await ConfigurationStoreProtocolClient
            .Create(endpoint, new ConfigurationClientCredential(credential))
            .GetNamespaceAsync(name, cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask StoreTrustedIssuerAsync(
        Uri endpoint,
        string credential,
        string owner,
        string issuer,
        ReadOnlyMemory<byte> publicKey,
        CancellationToken cancellationToken = default)
    {
        byte[] identity = Encoding.UTF8.GetBytes(owner + "\n" + issuer + "\n");
        byte[] commandHash;
        try
        {
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            hasher.AppendData(identity);
            hasher.AppendData(publicKey.Span);
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
            publicKey);
        try
        {
            await SecretStoreProtocolClient
                .Create(endpoint, new SecretClientCredential(credential))
                .SendCommandAsync(command, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(commandHash);
        }
    }
}
