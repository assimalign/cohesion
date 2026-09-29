using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Security;
using System.Threading;
using System.Threading.Tasks;

using ResourceCommand = Assimalign.Cohesion.Hosting.Resources.ResourceCommand;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.ControlPlane.Tests;

/// <summary>
/// An <see cref="IApplicationCallerAuthenticator"/> that answers from a configured function and
/// appends its name to a shared log on every call, so a test can observe pipeline order.
/// </summary>
internal sealed class RecordingCallerAuthenticator : IApplicationCallerAuthenticator
{
    private readonly string _name;
    private readonly ConcurrentQueue<string> _log;
    private readonly Func<ApplicationCallerRequest, ApplicationCallerResult> _authenticate;

    /// <summary>
    /// Initializes a new instance of the <see cref="RecordingCallerAuthenticator"/> class.
    /// </summary>
    /// <param name="name">The name recorded in <paramref name="log"/> for each call.</param>
    /// <param name="log">The log shared by every authenticator of one pipeline.</param>
    /// <param name="authenticate">The result for a presented credential.</param>
    public RecordingCallerAuthenticator(
        string name,
        ConcurrentQueue<string> log,
        Func<ApplicationCallerRequest, ApplicationCallerResult> authenticate)
    {
        _name = name;
        _log = log;
        _authenticate = authenticate;
    }

    public ConcurrentQueue<ApplicationCallerRequest> Requests { get; } = new();

    public ValueTask<ApplicationCallerResult> AuthenticateAsync(
        ApplicationCallerRequest request,
        IReadOnlyList<TrustedIssuer> trustedIssuers,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _log.Enqueue(_name);
        Requests.Enqueue(request);
        return ValueTask.FromResult(_authenticate(request));
    }
}

/// <summary>
/// An identity provider's issuer for the purposes it is given: it mints
/// <c>idp:&lt;purpose&gt;:&lt;application&gt;</c> bearer credentials and defers the rest.
/// </summary>
internal sealed class IdentityProviderCredentialIssuer : IApplicationCredentialIssuer
{
    private readonly HashSet<ApplicationCredentialPurpose> _purposes;

    /// <summary>
    /// Initializes a new instance of the <see cref="IdentityProviderCredentialIssuer"/> class.
    /// </summary>
    /// <param name="purposes">The purposes the identity provider issues.</param>
    public IdentityProviderCredentialIssuer(params ApplicationCredentialPurpose[] purposes)
    {
        _purposes = new HashSet<ApplicationCredentialPurpose>(purposes);
    }

    public ConcurrentQueue<ApplicationCredentialRequest> Requests { get; } = new();

    public ValueTask<ApplicationCredential?> IssueAsync(
        ApplicationCredentialRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Requests.Enqueue(request);
        return ValueTask.FromResult(_purposes.Contains(request.Purpose)
            ? new ApplicationCredential("Bearer", $"idp:{request.Purpose}:{request.Application}", DateTimeOffset.UtcNow.AddHours(1))
            : null);
    }
}

/// <summary>
/// A catch-all command client that records the address of every delivery, applied or deleted.
/// </summary>
internal sealed class AddressRecordingCommandClient : IGatewayResourceCommandClient
{
    public string ResourceKind => IGatewayResourceCommandClient.AnyKind;

    public ConcurrentQueue<(string Key, Uri Address)> Addresses { get; } = new();

    public ValueTask<ResourceCommandResult> ApplyAsync(
        Uri address,
        string bearerToken,
        ResourceCommand command,
        RemoteCertificateValidationCallback? serverCertificateValidator,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Addresses.Enqueue((command.Key, address));
        return ValueTask.FromResult(new ResourceCommandResult(ResourceCommandStatus.Applied, "applied"));
    }

    public ValueTask<ResourceCommandResult> DeleteAsync(
        Uri address,
        string bearerToken,
        ResourceCommand command,
        RemoteCertificateValidationCallback? serverCertificateValidator,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Addresses.Enqueue((command.Key, address));
        return ValueTask.FromResult(new ResourceCommandResult(ResourceCommandStatus.Applied, "removed"));
    }
}
