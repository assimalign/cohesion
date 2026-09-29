using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting.Resources;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

public abstract partial class ApplicationGateway
{
    private const string bearerScheme = "Bearer";

    // Resource-audience credentials for the current reconcile pass, keyed by application, resource,
    // and purpose. A null purpose holds the default issuer's credential for the resource, which
    // ResourceBootstrap and ResourceAccess share: its claims are identical for both, so one token
    // serves the resource's bootstrap and every gateway call to it during the pass.
    private readonly Dictionary<ResourceCredentialKey, ApplicationCredential> _resourceCredentials = new();

    // Every credential the gateway mints goes through here: the application's registered
    // IApplicationCredentialIssuer first, then - when none is registered or it defers with null -
    // the default ES256 application-key issuer (the application's trust state).
    private async ValueTask<ApplicationCredential> IssueCredentialAsync(
        IApplicationModel model,
        string audience,
        string subject,
        ApplicationCredentialPurpose purpose,
        TimeSpan lifetime,
        CancellationToken cancellationToken)
    {
        var request = new ApplicationCredentialRequest(model.Name, Name, audience, subject, purpose, lifetime);
        return await IssueRegisteredCredentialAsync(model, request, cancellationToken).ConfigureAwait(false)
            ?? IssueDefaultCredential(request);
    }

    private static async ValueTask<ApplicationCredential?> IssueRegisteredCredentialAsync(
        IApplicationModel model,
        ApplicationCredentialRequest request,
        CancellationToken cancellationToken)
    {
        IApplicationCredentialIssuer? issuer = GetProviders(model).CredentialIssuer;
        return issuer is null
            ? null
            : await issuer.IssueAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private ApplicationCredential IssueDefaultCredential(ApplicationCredentialRequest request) =>
        GetTrustState(request.Application).Issue(request, _options.TimeProvider.GetUtcNow());

    // The credential whose audience is one of the application's resources: its bootstrap
    // credential (ResourceBootstrap) or the gateway's credential for calling it (ResourceAccess).
    // Subject = the gateway, lifetime = BootstrapCredentialLifetime, cached for the pass. A cached
    // credential that has expired is reissued rather than presented: the served control plane
    // dispatches peer commands with this cache long after the pass that filled it, and an
    // identity provider's credential may live far shorter than the requested lifetime.
    private async ValueTask<ApplicationCredential> GetOrIssueResourceCredentialAsync(
        IApplicationModel model,
        ResourceName resource,
        ApplicationCredentialPurpose purpose,
        CancellationToken cancellationToken)
    {
        var key = new ResourceCredentialKey(model.Name, resource, purpose);
        DateTimeOffset now = _options.TimeProvider.GetUtcNow();
        lock (_credentialGate)
        {
            if (_resourceCredentials.TryGetValue(key, out ApplicationCredential? cached) &&
                IsUnexpired(cached, now))
            {
                return cached;
            }
        }

        var request = new ApplicationCredentialRequest(
            model.Name,
            Name,
            resource.ToString(),
            Name.ToString(),
            purpose,
            _options.BootstrapCredentialLifetime);
        // The issuer runs outside the gate; a concurrent caller that cached first wins, so every
        // caller in the pass observes one credential per key.
        ApplicationCredential? issued = await IssueRegisteredCredentialAsync(model, request, cancellationToken)
            .ConfigureAwait(false);
        lock (_credentialGate)
        {
            if (_resourceCredentials.TryGetValue(key, out ApplicationCredential? raced) &&
                IsUnexpired(raced, now))
            {
                return raced;
            }

            if (issued is null)
            {
                var shared = new ResourceCredentialKey(model.Name, resource, null);
                if (!_resourceCredentials.TryGetValue(shared, out issued) || !IsUnexpired(issued, now))
                {
                    issued = IssueDefaultCredential(request);
                    _resourceCredentials[shared] = issued;
                }
            }

            _resourceCredentials[key] = issued;
            return issued;
        }
    }

    // A cached credential is reused only while it is still valid at the time of use.
    private static bool IsUnexpired(ApplicationCredential credential, DateTimeOffset now) =>
        credential.ExpiresAt > now;

    // A credential presented to a peer gateway's control plane (RemoteCommand, PeerControlPlane)
    // or exported to a developer (Developer): audience cohesion-export, DeveloperTokenLifetime.
    private async ValueTask<string> IssueExportCredentialAsync(
        IApplicationModel model,
        string subject,
        ApplicationCredentialPurpose purpose,
        CancellationToken cancellationToken)
    {
        ApplicationCredential credential = await IssueCredentialAsync(
                model,
                ResourceCredentialProfile.ExportAudience,
                subject,
                purpose,
                _options.DeveloperTokenLifetime,
                cancellationToken)
            .ConfigureAwait(false);
        return RequireBearer(credential, purpose);
    }

    // The bootstrap file, the resource probes, the command clients, a provider connection, and the
    // peer control-plane client all present the credential as 'Authorization: Bearer <value>'. Only
    // the telemetry headers document names its scheme, so it is the one carrier that accepts another.
    private static string RequireBearer(ApplicationCredential credential, ApplicationCredentialPurpose purpose)
    {
        if (!string.Equals(credential.Scheme, bearerScheme, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"The registered credential issuer returned a '{credential.Scheme}' credential for purpose " +
                $"'{purpose}', but that credential is presented as a bearer token. Return a Bearer credential, " +
                "or null to use the default ES256 application-key issuer.");
        }

        if (credential.Value.AsSpan().IndexOfAny("\r\n") >= 0)
        {
            throw new InvalidOperationException(
                $"The registered credential issuer returned a credential for purpose '{purpose}' whose value " +
                "contains a line break; a bearer credential must be a single header-safe token.");
        }

        return credential.Value;
    }

    private void RemoveResourceCredentials(ApplicationName application)
    {
        lock (_credentialGate)
        {
            var keys = new List<ResourceCredentialKey>();
            foreach (ResourceCredentialKey key in _resourceCredentials.Keys)
            {
                if (key.Application == application)
                {
                    keys.Add(key);
                }
            }

            for (int index = 0; index < keys.Count; index++)
            {
                _resourceCredentials.Remove(keys[index]);
            }
        }
    }

    private readonly record struct ResourceCredentialKey(
        ApplicationName Application,
        ResourceName Resource,
        ApplicationCredentialPurpose? Purpose);
}
