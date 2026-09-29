using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.ApplicationModel;
using HostCommand = Assimalign.Cohesion.Hosting.Resources.ResourceCommand;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Tests;

/// <summary>
/// An <see cref="IApplicationCredentialIssuer"/> that records every request and answers from a
/// configured function; a function that returns <see langword="null"/> defers to the default issuer.
/// </summary>
internal sealed class RecordingCredentialIssuer : IApplicationCredentialIssuer
{
    private readonly Func<ApplicationCredentialRequest, ApplicationCredential?> _issue;

    /// <summary>
    /// Initializes a new instance of the <see cref="RecordingCredentialIssuer"/> class.
    /// </summary>
    /// <param name="issue">Mints a credential for a request, or returns <see langword="null"/> to defer.</param>
    public RecordingCredentialIssuer(Func<ApplicationCredentialRequest, ApplicationCredential?>? issue = null)
    {
        _issue = issue ?? (static _ => null);
    }

    public ConcurrentQueue<ApplicationCredentialRequest> Requests { get; } = new();

    public ValueTask<ApplicationCredential?> IssueAsync(
        ApplicationCredentialRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Requests.Enqueue(request);
        return ValueTask.FromResult(_issue(request));
    }
}

/// <summary>
/// A time source whose wall clock stands still until a test moves it, so issued credentials are
/// deterministic.
/// </summary>
internal sealed class FixedUtcTimeProvider : TimeProvider
{
    private DateTimeOffset _utcNow;

    /// <summary>
    /// Initializes a new instance of the <see cref="FixedUtcTimeProvider"/> class.
    /// </summary>
    /// <param name="utcNow">The instant every <see cref="GetUtcNow"/> call returns until <see cref="Advance"/>.</param>
    public FixedUtcTimeProvider(DateTimeOffset utcNow)
    {
        _utcNow = utcNow;
    }

    public override DateTimeOffset GetUtcNow() => _utcNow;

    /// <summary>Moves the clock forward.</summary>
    /// <param name="duration">How far to move it.</param>
    public void Advance(TimeSpan duration) => _utcNow = _utcNow.Add(duration);
}

/// <summary>
/// An <see cref="IGatewayTrustKeyRepository"/> that hands the gateway a copy of one known P-256 key,
/// so a test can mint the credential the default issuer must produce.
/// </summary>
internal sealed class FixedTrustKeyRepository : IGatewayTrustKeyRepository
{
    private readonly ECParameters _parameters;

    /// <summary>
    /// Initializes a new instance of the <see cref="FixedTrustKeyRepository"/> class.
    /// </summary>
    /// <param name="signingKey">The key whose private parameters every load returns.</param>
    public FixedTrustKeyRepository(ECDsa signingKey)
    {
        _parameters = signingKey.ExportParameters(includePrivateParameters: true);
    }

    public Task<ECDsa> LoadOrCreateAsync(
        ApplicationName application,
        ResourceName gateway,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(ECDsa.Create(_parameters));
    }

    public Task<ECDsa> RotateAsync(
        ApplicationName application,
        ResourceName gateway,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(ECDsa.Create(_parameters));
    }
}

/// <summary>
/// A peer gateway client that records the bearer credential of every discovery read
/// (<see cref="ApplicationCredentialPurpose.PeerControlPlane"/>) and command delivery
/// (<see cref="ApplicationCredentialPurpose.RemoteCommand"/>), and answers with a configured export.
/// </summary>
internal sealed class RecordingPeerControlPlaneClient : IAuthenticatedControlPlaneClient
{
    private readonly ApplicationExportDocument _export;

    /// <summary>
    /// Initializes a new instance of the <see cref="RecordingPeerControlPlaneClient"/> class.
    /// </summary>
    /// <param name="export">The peer export every discovery read returns.</param>
    public RecordingPeerControlPlaneClient(ApplicationExportDocument export)
    {
        _export = export;
    }

    public ConcurrentQueue<string> DiscoveryCredentials { get; } = new();

    public ConcurrentQueue<string> CommandCredentials { get; } = new();

    public ValueTask<ApplicationExportDocument> GetApplicationAsync(
        Uri address,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The gateway always calls the authenticated overload.");

    public ValueTask<ApplicationExportDocument> GetApplicationAsync(
        Uri address,
        string bearerToken,
        IReadOnlyList<TrustedIssuer> trustedIssuers,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DiscoveryCredentials.Enqueue(bearerToken);
        return ValueTask.FromResult(_export);
    }

    public ValueTask<ResourceCommandResult> ApplyCommandAsync(
        Uri address,
        ResourceName resource,
        string bearerToken,
        HostCommand command,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CommandCredentials.Enqueue(bearerToken);
        return ValueTask.FromResult(new ResourceCommandResult(ResourceCommandStatus.Applied, "applied by the peer"));
    }

    public ValueTask<ResourceCommandResult> DeleteCommandAsync(
        Uri address,
        ResourceName resource,
        string bearerToken,
        HostCommand command,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CommandCredentials.Enqueue(bearerToken);
        return ValueTask.FromResult(new ResourceCommandResult(ResourceCommandStatus.Applied, "removed by the peer"));
    }
}

/// <summary>
/// Realizes every planned resource as Running on its manifest endpoints (host <c>127.0.0.1</c>) and
/// records the inputs and telemetry the gateway prepared for it.
/// </summary>
internal sealed class CredentialCapturingController : IApplicationResourceController
{
    public ConcurrentDictionary<string, ResourceInputs> Inputs { get; } = new(StringComparer.Ordinal);

    public ConcurrentDictionary<string, IResourceTelemetry?> Telemetry { get; } = new(StringComparer.Ordinal);

    public bool CanRealize(ResourcePlan plan, out string? reason)
    {
        reason = null;
        return true;
    }

    public Task ReconcileAsync(IResourceControlContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string name = context.Resource.Name.ToString();
        Inputs[name] = context.Inputs!;
        Telemetry[name] = context.GetTelemetry();
        var observed = new List<ResourceEndpoint>();
        foreach (ResourceManifest manifest in context.Model.Manifests)
        {
            if (manifest.Name == context.Resource.Name)
            {
                foreach (ResourceManifestEndpoint endpoint in manifest.Endpoints)
                {
                    observed.Add(new ResourceEndpoint(endpoint.Name, endpoint.Scheme, endpoint.ContainerPort, Host: "127.0.0.1"));
                }
            }
        }

        context.State.SetState(context.Resource.Id, ResourceLifecycle.Running, observedEndpoints: observed);
        return Task.CompletedTask;
    }

    public Task StopAsync(IResourceControlContext context, CancellationToken cancellationToken = default)
    {
        context.State.SetState(context.Resource.Id, ResourceLifecycle.Stopped);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(IResourceControlContext context, CancellationToken cancellationToken = default) =>
        StopAsync(context, cancellationToken);
}
