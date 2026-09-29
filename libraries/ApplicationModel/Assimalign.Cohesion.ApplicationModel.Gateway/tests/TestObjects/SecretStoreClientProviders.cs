using System;
using System.Buffers;
using System.Net.Http;
using System.Net.Security;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.ApplicationModel;
using Assimalign.Cohesion.SecretStore.Client;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Tests;

// Test doubles for the real-host tests. The gateway knows no store wire protocol; these doubles
// speak the SecretStore test host's protocol through the SecretStore client the test project
// references, standing in for the opt-in orchestration package a real gateway would register.

/// <summary>Reads Secret mounts from a SecretStore test host.</summary>
internal sealed class SecretStoreClientSourceProvider : IResourceSourceProvider
{
    public string? ResourceKind => "SecretStore";

    public async ValueTask<ReadOnlyMemory<byte>> ReadSecretAsync(
        ResourceSourceRequest request,
        CancellationToken cancellationToken = default)
    {
        ResourceProviderConnection store = request.Store
            ?? throw new ArgumentException("A SecretStore source needs a store connection.", nameof(request));
        using HttpMessageInvoker transport = SecretStoreTestTransport.Create(store.ServerCertificateValidator);
        return await SecretStoreTestTransport.CreateClient(store, transport)
            .GetSecretAsync(request.Key, cancellationToken)
            .ConfigureAwait(false);
    }
}

/// <summary>Issues endpoint leaves from a SecretStore test host's certificate authority.</summary>
internal sealed class SecretStoreClientCertificateAuthority : IResourceCertificateAuthority
{
    public string? ResourceKind => "SecretStore";

    public int IssueCount { get; private set; }

    public async ValueTask<ResourceCertificate> IssueAsync(
        ResourceCertificateRequest request,
        ResourceProviderConnection? authority,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authority);
        IssueCount++;
        using HttpMessageInvoker transport = SecretStoreTestTransport.Create(authority.ServerCertificateValidator);
        ISecretStoreClient client = SecretStoreTestTransport.CreateClient(authority, transport);
        string leaf = await client.GetCertificateAsync("certs/" + request.LeafName, cancellationToken).ConfigureAwait(false);
        string root = await client.GetCertificateAsync("ca/root", cancellationToken).ConfigureAwait(false);
        return new ResourceCertificate(leaf, root);
    }
}

/// <summary>
/// Rewrites a declared <c>secretstore.add-secret</c> payload into the delivered
/// <c>{"path","source","resolvedValue"}</c> envelope the SecretStore test host accepts.
/// </summary>
internal sealed class AddSecretInputResolver : IResourceCommandInputResolver
{
    public string CommandKind => "secretstore.add-secret";

    public async ValueTask<ReadOnlyMemory<byte>> ResolveAsync(
        ResourceCommandInput declared,
        IResourceSourceResolver sources,
        CancellationToken cancellationToken = default)
    {
        string source;
        using (JsonDocument document = JsonDocument.Parse(declared.Payload))
        {
            source = document.RootElement.GetProperty("source").GetString()
                ?? throw new InvalidOperationException("secretstore.add-secret requires a nonblank source.");
        }

        ResourceMountInput input = await sources
            .ResolveAsync(source, ResourceMountKind.Secret, cancellationToken)
            .ConfigureAwait(false);
        if (!input.IsResolved)
        {
            throw new InvalidOperationException(
                $"secretstore.add-secret source '{source}' is unresolved: {input.UnresolvedReason}");
        }

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("path", declared.Key);
            writer.WriteString("source", source);
            writer.WriteBase64String("resolvedValue", input.Content.Span);
            writer.WriteEndObject();
        }

        return buffer.WrittenMemory.ToArray();
    }
}

internal static class SecretStoreTestTransport
{
    internal static HttpMessageInvoker Create(RemoteCertificateValidationCallback? validator)
    {
        var handler = new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false };
        if (validator is not null)
        {
            handler.SslOptions.RemoteCertificateValidationCallback = validator;
        }

        return new HttpMessageInvoker(handler, disposeHandler: true);
    }

    internal static ISecretStoreClient CreateClient(ResourceProviderConnection connection, HttpMessageInvoker transport) =>
        SecretStoreClient.CreateForControlPlane(
            connection.ControlPlaneAddress,
            new ClientCredential(connection.BearerCredential),
            transport);
}
