using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Tests;

/// <summary>
/// An <see cref="IResourceSourceProvider"/> that records every request and answers from
/// configured values, so gateway tests observe exactly what the gateway hands a provider.
/// </summary>
internal sealed class RecordingSourceProvider : IResourceSourceProvider
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RecordingSourceProvider"/> class.
    /// </summary>
    /// <param name="resourceKind">The manifest kind a bound resource must have, or <see langword="null"/>.</param>
    public RecordingSourceProvider(string? resourceKind = null)
    {
        ResourceKind = resourceKind;
    }

    public string? ResourceKind { get; }

    public List<ResourceSourceRequest> Requests { get; } = new();

    public ReadOnlyMemory<byte> Secret { get; init; } = Encoding.UTF8.GetBytes("store-secret");

    public IReadOnlyDictionary<string, string?> Configuration { get; init; } =
        new Dictionary<string, string?> { ["zeta"] = null, ["alpha"] = "on" };

    public ResourceCertificate? Certificate { get; init; }

    public Exception? Failure { get; init; }

    public ValueTask<ReadOnlyMemory<byte>> ReadSecretAsync(
        ResourceSourceRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Requests.Add(request);
        return Failure is null
            ? ValueTask.FromResult(Secret)
            : ValueTask.FromException<ReadOnlyMemory<byte>>(Failure);
    }

    public ValueTask<ResourceCertificate> ReadCertificateAsync(
        ResourceSourceRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Requests.Add(request);
        Exception? failure = Failure ?? (Certificate is null
            ? new NotSupportedException("Certificate issuance is unavailable from this test store.")
            : null);
        return failure is null
            ? ValueTask.FromResult(Certificate!)
            : ValueTask.FromException<ResourceCertificate>(failure);
    }

    public ValueTask<IReadOnlyDictionary<string, string?>> ReadConfigurationAsync(
        ResourceSourceRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Requests.Add(request);
        return Failure is null
            ? ValueTask.FromResult(Configuration)
            : ValueTask.FromException<IReadOnlyDictionary<string, string?>>(Failure);
    }
}

/// <summary>
/// An <see cref="IResourceCertificateAuthority"/> that records every leaf request and its
/// connection, and returns a configured certificate or failure.
/// </summary>
internal sealed class RecordingCertificateAuthority : IResourceCertificateAuthority
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RecordingCertificateAuthority"/> class.
    /// </summary>
    /// <param name="certificate">The certificate every request returns, or <see langword="null"/> to refuse.</param>
    /// <param name="resourceKind">The manifest kind a bound resource must have, or <see langword="null"/>.</param>
    public RecordingCertificateAuthority(ResourceCertificate? certificate = null, string? resourceKind = null)
    {
        Certificate = certificate;
        ResourceKind = resourceKind;
    }

    public string? ResourceKind { get; }

    public ResourceCertificate? Certificate { get; }

    public Exception? Failure { get; init; }

    public List<(ResourceCertificateRequest Request, ResourceProviderConnection? Authority)> Requests { get; } = new();

    public ValueTask<ResourceCertificate> IssueAsync(
        ResourceCertificateRequest request,
        ResourceProviderConnection? authority,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Requests.Add((request, authority));
        Exception? failure = Failure ?? (Certificate is null
            ? new NotSupportedException("The test authority issues no certificates.")
            : null);
        return failure is null
            ? ValueTask.FromResult(Certificate!)
            : ValueTask.FromException<ResourceCertificate>(failure);
    }
}

/// <summary>
/// An <see cref="ITrustedIssuerStore"/> that records every read and grant and answers reads
/// from configured issuers.
/// </summary>
internal sealed class RecordingTrustedIssuerStore : ITrustedIssuerStore
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RecordingTrustedIssuerStore"/> class.
    /// </summary>
    /// <param name="issuers">The issuers every read returns, or <see langword="null"/> for "nothing persisted".</param>
    /// <param name="resourceKind">The manifest kind a bound resource must have, or <see langword="null"/>.</param>
    public RecordingTrustedIssuerStore(IReadOnlyList<TrustedIssuer>? issuers = null, string? resourceKind = null)
    {
        Issuers = issuers;
        ResourceKind = resourceKind;
    }

    public string? ResourceKind { get; }

    public IReadOnlyList<TrustedIssuer>? Issuers { get; }

    public Exception? ReadFailure { get; init; }

    public List<ResourceProviderConnection?> Reads { get; } = new();

    public List<(ResourceProviderConnection? Store, string Owner, TrustedIssuer Issuer)> Adds { get; } = new();

    public ValueTask<IReadOnlyList<TrustedIssuer>?> ReadAsync(
        ResourceProviderConnection? store,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Reads.Add(store);
        return ReadFailure is null
            ? ValueTask.FromResult(Issuers)
            : ValueTask.FromException<IReadOnlyList<TrustedIssuer>?>(ReadFailure);
    }

    public ValueTask AddAsync(
        ResourceProviderConnection? store,
        string owner,
        TrustedIssuer issuer,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Adds.Add((store, owner, issuer));
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// An <see cref="IResourceCommandInputResolver"/> that records every declared command, resolves
/// its configured sources as Secret values through the gateway's <see cref="IResourceSourceResolver"/>,
/// and delivers them joined by <c>|</c>, or refuses with a configured failure.
/// </summary>
internal sealed class RecordingCommandInputResolver : IResourceCommandInputResolver
{
    private readonly IReadOnlyList<string> _sources;

    /// <summary>
    /// Initializes a new instance of the <see cref="RecordingCommandInputResolver"/> class.
    /// </summary>
    /// <param name="commandKind">The command kind the resolver serves.</param>
    /// <param name="sources">The source expressions resolved into the delivered payload.</param>
    public RecordingCommandInputResolver(string commandKind, IReadOnlyList<string>? sources = null)
    {
        CommandKind = commandKind;
        _sources = sources ?? Array.Empty<string>();
    }

    public string CommandKind { get; }

    public Exception? Failure { get; init; }

    public List<ResourceCommandInput> Declared { get; } = new();

    public async ValueTask<ReadOnlyMemory<byte>> ResolveAsync(
        ResourceCommandInput declared,
        IResourceSourceResolver sources,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Declared.Add(declared);
        if (Failure is not null)
        {
            throw Failure;
        }

        var parts = new List<string>(_sources.Count);
        foreach (string source in _sources)
        {
            ResourceMountInput input = await sources
                .ResolveAsync(source, ResourceMountKind.Secret, cancellationToken)
                .ConfigureAwait(false);
            if (!input.IsResolved)
            {
                throw new InvalidOperationException($"Source '{source}' is unresolved: {input.UnresolvedReason}");
            }

            parts.Add(Encoding.UTF8.GetString(input.Content.Span));
        }

        return Encoding.UTF8.GetBytes(string.Join('|', parts));
    }
}
