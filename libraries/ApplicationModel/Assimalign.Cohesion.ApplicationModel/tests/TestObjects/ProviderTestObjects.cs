using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.ApplicationModel.Tests;

/// <summary>
/// A source provider that supplies secrets only, leaving the certificate and configuration
/// members on their default <see cref="NotSupportedException"/> bodies.
/// </summary>
internal sealed class SecretOnlySourceProvider : IResourceSourceProvider
{
    public SecretOnlySourceProvider(string? resourceKind = null)
    {
        ResourceKind = resourceKind;
    }

    public string? ResourceKind { get; }

    public ValueTask<ReadOnlyMemory<byte>> ReadSecretAsync(
        ResourceSourceRequest request,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<ReadOnlyMemory<byte>>(new byte[] { 1, 2, 3 });
}

/// <summary>A source provider that overrides none of the read members.</summary>
internal sealed class EmptySourceProvider : IResourceSourceProvider
{
    public string? ResourceKind => null;
}

internal sealed class FakeCertificateAuthority : IResourceCertificateAuthority
{
    public FakeCertificateAuthority(string? resourceKind = null)
    {
        ResourceKind = resourceKind;
    }

    public string? ResourceKind { get; }

    public ValueTask<ResourceCertificate> IssueAsync(
        ResourceCertificateRequest request,
        ResourceProviderConnection? authority,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new ResourceCertificate("leaf", "root"));
}

internal sealed class FakeTrustedIssuerStore : ITrustedIssuerStore
{
    public FakeTrustedIssuerStore(string? resourceKind = null)
    {
        ResourceKind = resourceKind;
    }

    public string? ResourceKind { get; }

    public ValueTask<IReadOnlyList<TrustedIssuer>?> ReadAsync(
        ResourceProviderConnection? store,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<IReadOnlyList<TrustedIssuer>?>(null);

    public ValueTask AddAsync(
        ResourceProviderConnection? store,
        string owner,
        TrustedIssuer issuer,
        CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;
}

internal sealed class FakeCommandInputResolver : IResourceCommandInputResolver
{
    public FakeCommandInputResolver(string commandKind = "test.resolve")
    {
        CommandKind = commandKind;
    }

    public string CommandKind { get; }

    public ValueTask<ReadOnlyMemory<byte>> ResolveAsync(
        ResourceCommandInput declared,
        IResourceSourceResolver sources,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(declared.Payload);
}

internal sealed class FakeCallerAuthenticator : IApplicationCallerAuthenticator
{
    public ValueTask<ApplicationCallerResult> AuthenticateAsync(
        ApplicationCallerRequest request,
        IReadOnlyList<TrustedIssuer> trustedIssuers,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new ApplicationCallerResult(ApplicationCallerStatus.NoResult, null, null));
}

internal sealed class FakeCredentialIssuer : IApplicationCredentialIssuer
{
    public ValueTask<ApplicationCredential?> IssueAsync(
        ApplicationCredentialRequest request,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<ApplicationCredential?>(null);
}
