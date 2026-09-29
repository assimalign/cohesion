using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.ApplicationModel.Gateway.Internal;
using Assimalign.Cohesion.Hosting.Resources;

using HostingMount = Assimalign.Cohesion.Hosting.Resources.ResourceMount;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

public abstract partial class ApplicationGateway
{
    private readonly Dictionary<ApplicationName, GatewayCertificateAuthority> _certificateAuthorities = new();

    private GatewayCertificateAuthority GetCertificateAuthority(ApplicationName application)
    {
        lock (_certificateAuthorities)
        {
            if (!_certificateAuthorities.TryGetValue(application, out GatewayCertificateAuthority? authority))
            {
                authority = new GatewayCertificateAuthority(GetApplicationDirectory(application), application);
                _certificateAuthorities.Add(application, authority);
            }
            return authority;
        }
    }

    /// <inheritdoc/>
    RemoteCertificateValidationCallback? IResourceTransportTrustProvider.CreateOutboundTrustValidator(ApplicationName application) =>
        CreateOutboundTrustValidator(application);

    private RemoteCertificateValidationCallback? CreateOutboundTrustValidator(ApplicationName application)
    {
        GatewayCertificateAuthority authority = GetCertificateAuthority(application);
        if (authority.ExportAnchors().IsEmpty)
        {
            return null;
        }
        var context = new ResourceContext(application.ToString(), null, null, null, null, null, null, null, null,
            default, default, new Dictionary<string, string?> { [AppEnvironment.Variables.TrustBundlePath] = authority.TrustPath });
        return context.CreateOutboundTrustValidator();
    }

    // Issues the leaf for an endpoint whose certificate mount declares no source (owner decision
    // 3). A registered certificate authority issues it once its bound resource is Running. Without
    // one, only Local may use the gateway development authority; every other environment fails
    // loudly rather than silently downgrading. The authority resource's own leaf always comes from
    // the gateway authority, in every environment, because it cannot issue its first certificate.
    private async ValueTask<ResourceMountInput> ResolveDefaultCertificateAsync(
        IResourceControlContext context, ResourcePlan plan, MountBinding mount, CancellationToken cancellationToken)
    {
        IApplicationModel model = context.Model;
        string endpointName = plan.Container.Ports.First(port => string.Equals(port.Certificate, mount.Mount, StringComparison.Ordinal)).Endpoint;
        string leafName = $"{plan.Resource}-{endpointName}";
        IReadOnlyList<string> hosts = CollectCertificateHosts(context, plan, mount);
        ResourceProviderBinding<IResourceCertificateAuthority>? binding = GetProviders(model).CertificateAuthority;

        if (binding is not null && binding.Resource is ResourceName authorityResource && authorityResource == plan.Resource)
        {
            return IssueDevelopmentCertificate(model, leafName, hosts);
        }

        if (binding is null)
        {
            if (model.Environment.IsLocal)
            {
                return IssueDevelopmentCertificate(model, leafName, hosts);
            }

            throw new InvalidOperationException(
                $"Endpoint '{endpointName}' on resource '{plan.Resource}' of application '{model.Name}' declares " +
                $"certificate mount '{mount.Mount}' without a source, but no certificate authority is registered. " +
                "The gateway development certificate authority issues leaves only in Local. Register a " +
                "certificate authority in builder.Providers.CertificateAuthority (a store's " +
                "Assimalign.Cohesion.<Area>.ApplicationModel.Orchestration package does it through " +
                "builder.Use<Area>(store).AsCertificateAuthority(); an application-set member registers it " +
                "in its own AddApplication(..., member => member.Use<Area>(\"<store>\").AsCertificateAuthority()) " +
                "callback), or give the mount an explicit source.");
        }

        var request = new ResourceCertificateRequest(model.Name, plan.Resource, endpointName, leafName, hosts);
        (ResourceMountInput? issued, string? failure) = await IssueFromAuthorityAsync(
                context, mount, binding, request, cancellationToken)
            .ConfigureAwait(false);
        if (issued is not null)
        {
            return issued;
        }

        if (model.Environment.IsLocal)
        {
            // In Local only, a transport that precedes the authority's readiness or enrollment
            // bootstraps with the gateway development identity until the authority can issue.
            return IssueDevelopmentCertificate(model, leafName, hosts);
        }

        throw new InvalidOperationException(
            $"Endpoint '{endpointName}' on resource '{plan.Resource}' of application '{model.Name}' could not " +
            $"receive leaf '{leafName}' from certificate authority '{DescribeBinding(binding)}': {failure} " +
            "Outside Local the gateway never falls back to its development certificate authority.");
    }

    private async ValueTask<(ResourceMountInput? Input, string? Failure)> IssueFromAuthorityAsync(
        IResourceControlContext context,
        MountBinding mount,
        ResourceProviderBinding<IResourceCertificateAuthority> binding,
        ResourceCertificateRequest request,
        CancellationToken cancellationToken)
    {
        IApplicationModel model = context.Model;
        ResourceProviderConnection? connection = null;
        if (binding.Resource is ResourceName resource)
        {
            string? unavailable = TryResolveAuthorityAddress(context, resource, out ResourceManifest? manifest, out Uri? endpoint);
            if (unavailable is not null)
            {
                return (null, unavailable);
            }

            connection = await CreateProviderConnectionAsync(model, manifest!, endpoint!, cancellationToken)
                .ConfigureAwait(false);
        }

        ResourceCertificate certificate;
        try
        {
            certificate = await binding.Provider
                .IssueAsync(request, connection, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidDataException(
                    $"Certificate authority '{binding.Provider.GetType().Name}' returned no certificate.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsProviderFailure(exception))
        {
            return (null, exception.Message);
        }

        if (!TryValidateCertificate(mount, Encoding.UTF8.GetBytes(certificate.CertificatePem), out string? unusable))
        {
            return (null, $"The issued certificate is unusable: {unusable}");
        }

        GetCertificateAuthority(model.Name).AddAnchors(certificate.TrustAnchorsPem);
        return (ResourceMountInput.Resolved(null, Encoding.UTF8.GetBytes(certificate.CertificatePem)), null);
    }

    // Resolves the application's own resource bound as its certificate authority once it is
    // Running with an observed control-plane endpoint. Returns why it cannot, or null; the caller
    // then mints the connection's ResourceAccess credential.
    private static string? TryResolveAuthorityAddress(
        IResourceControlContext context,
        ResourceName resource,
        out ResourceManifest? authorityManifest,
        out Uri? controlPlaneAddress)
    {
        authorityManifest = null;
        controlPlaneAddress = null;
        IApplicationModel model = context.Model;
        for (int index = 0; index < model.Manifests.Count; index++)
        {
            ResourceManifest manifest = model.Manifests[index];
            if (manifest.Application != model.Name || manifest.Name != resource)
            {
                continue;
            }

            IApplicationResource authority = model.Descriptors[index].Resource;
            ResourceLifecycle state = context.State.GetState(authority.Id);
            if (state != ResourceLifecycle.Running)
            {
                return $"Certificate authority resource '{resource}' is '{state}', not Running.";
            }

            if (!TryGetObservedControlPlaneAddress(manifest, context.State.GetObservedEndpoints(authority.Id), out Uri? endpoint) &&
                !(model.Environment.IsLocal && TryGetDeclaredDevelopmentAddress(manifest, out endpoint)))
            {
                return $"Certificate authority resource '{resource}' has no observed " +
                    $"'{manifest.ControlPlane.Endpoint}' control-plane endpoint.";
            }

            if (!CanSendCredential(model, endpoint, out string? securityFailure))
            {
                return securityFailure;
            }

            authorityManifest = manifest;
            controlPlaneAddress = endpoint;
            return null;
        }

        return $"Certificate authority resource '{resource}' is not a resource of application '{model.Name}'.";
    }

    private ResourceMountInput IssueDevelopmentCertificate(
        IApplicationModel model,
        string leafName,
        IReadOnlyList<string> hosts)
    {
        string bundle = GetCertificateAuthority(model.Name).Issue(leafName, hosts);
        return ResourceMountInput.Resolved(null, Encoding.UTF8.GetBytes(bundle));
    }

    // The names the leaf must cover: every observed host of the endpoints that use this mount, and
    // the planned HOST and PUBLIC_URL host of each. The development authority adds loopback names.
    private static IReadOnlyList<string> CollectCertificateHosts(
        IResourceControlContext context,
        ResourcePlan plan,
        MountBinding mount)
    {
        var hosts = new List<string>();
        var endpointNames = new HashSet<string>(plan.Container.Ports
            .Where(port => string.Equals(port.Certificate, mount.Mount, StringComparison.Ordinal))
            .Select(port => port.Endpoint), StringComparer.Ordinal);
        foreach (ResourceEndpoint observed in context.State.GetObservedEndpoints(context.Resource.Id))
        {
            if (endpointNames.Contains(observed.Name) && !string.IsNullOrWhiteSpace(observed.Host))
            {
                hosts.Add(observed.Host);
            }
        }

        foreach (PortBinding port in plan.Container.Ports.Where(port => string.Equals(port.Certificate, mount.Mount, StringComparison.Ordinal)))
        {
            if (plan.Container.Environment.TryGetValue(AppEnvironment.Variables.Endpoint(port.Endpoint, "HOST"), out string? host))
            {
                hosts.Add(host);
            }

            if (plan.Container.Environment.TryGetValue(AppEnvironment.Variables.Endpoint(port.Endpoint, "PUBLIC_URL"), out string? url) && Uri.TryCreate(url, UriKind.Absolute, out Uri? address))
            {
                hosts.Add(address.IdnHost);
            }
        }

        return hosts;
    }

    private static string DescribeBinding<T>(ResourceProviderBinding<T> binding)
        where T : class =>
        binding.Resource is ResourceName resource
            ? resource.ToString()
            : binding.Provider.GetType().Name;

    private static ResourceMountInput ValidateCertificateInput(ResourcePlan plan, MountBinding mount, ResourceMountInput input)
    {
        if (!input.IsResolved || input.Content.IsEmpty ||
            TryValidateCertificate(mount, input.Content, out string? failure))
        {
            return input;
        }

        return ResourceMountInput.Unresolved(input.Source ?? mount.Mount,
            $"Certificate for mount '{mount.Mount}' on resource '{plan.Resource}' is unusable: {failure}");
    }

    private static bool TryValidateCertificate(MountBinding mount, ReadOnlyMemory<byte> content, out string? failure)
    {
        try
        {
            var context = new ResourceContext(mounts: new Dictionary<string, HostingMount> { ["tls"] = HostingMount.FromBytes(content.Span) });
            context.TryGetEndpointCertificate(mount.Mount, out X509Certificate2? certificate, out X509Certificate2Collection chain);
            certificate?.Dispose();
            foreach (X509Certificate2 issuer in chain)
            {
                issuer.Dispose();
            }

            failure = null;
            return true;
        }
        catch (Exception exception) when (exception is CryptographicException or InvalidOperationException or IOException or ArgumentException)
        {
            failure = exception.Message;
            return false;
        }
    }
}
