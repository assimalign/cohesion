using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Core;
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

    private void ConfigureStoreTransport(ApplicationName application, Uri endpoint)
    {
        if (_options.StoreClient is GatewayStoreClient client)
        {
            client.SetTransportTrust(endpoint, CreateOutboundTrustValidator(application));
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
            default, default, new Dictionary<string, string?> { [ResourceEnvironment.TrustBundlePath] = authority.TrustPath });
        return context.CreateOutboundTrustValidator();
    }

    private async ValueTask<ResourceMountInput> ResolveDefaultCertificateAsync(
        IResourceControlContext context, ResourcePlan plan, MountBinding mount, CancellationToken cancellationToken)
    {
        string endpointName = plan.Container.Ports.First(port => string.Equals(port.Certificate, mount.Mount, StringComparison.Ordinal)).Endpoint;
        string leafName = $"{plan.Resource}-{endpointName}";
        GatewayCertificateAuthority authority = GetCertificateAuthority(context.Model.Name);
        if (TryGetOwnSecretStoreEndpoint(context.Model, out ResourceManifest? store, out Uri? endpoint) &&
            store is not null && endpoint is not null && store.Name != plan.Resource &&
            CanSendCredential(context.Model, endpoint, out _))
        {
            IApplicationResource? storeResource = context.Model.Descriptors.FirstOrDefault(candidate => candidate.Resource.Name == store.Name)?.Resource;
            if (storeResource is not null && context.State.GetState(storeResource.Id) == ResourceLifecycle.Running)
            {
                ConfigureStoreTransport(context.Model.Name, endpoint);
                string credential = GetOrIssueBootstrapToken(context.Model, store.Name);
                try
                {
                    string pem = await _options.StoreClient.ReadCertificateAsync(endpoint, credential, "certs/" + leafName, cancellationToken).ConfigureAwait(false);
                    ResourceMountInput input = ValidateCertificateInput(plan, mount, ResourceMountInput.Resolved(null, Encoding.UTF8.GetBytes(pem)));
                    if (input.IsResolved && !input.Content.IsEmpty)
                    {
                        string root = await _options.StoreClient.ReadCertificateAsync(endpoint, credential, "ca/root", cancellationToken).ConfigureAwait(false);
                        authority.AddAnchors(root);
                        return input;
                    }
                }
                catch (Exception exception) when (exception is HttpRequestException or InvalidDataException or NotSupportedException)
                {
                    // A running transport may precede CA enrollment; its dependents can bootstrap
                    // with the gateway identity until the store can issue application certificates.
                }
            }
        }
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
            if (plan.Container.Environment.TryGetValue(ResourceEnvironment.Endpoint(port.Endpoint, "HOST"), out string? host))
            {
                hosts.Add(host);
            }

            if (plan.Container.Environment.TryGetValue(ResourceEnvironment.Endpoint(port.Endpoint, "PUBLIC_URL"), out string? url) && Uri.TryCreate(url, UriKind.Absolute, out Uri? address))
            {
                hosts.Add(address.IdnHost);
            }
        }
        string bundle = authority.Issue(leafName, hosts);
        return ResourceMountInput.Resolved(null, Encoding.UTF8.GetBytes(bundle));
    }

    private static ResourceMountInput ValidateCertificateInput(ResourcePlan plan, MountBinding mount, ResourceMountInput input)
    {
        if (!input.IsResolved || input.Content.IsEmpty)
        {
            return input;
        }

        try
        {
            var context = new ResourceContext(mounts: new Dictionary<string, HostingMount> { ["tls"] = HostingMount.FromBytes(input.Content.Span) });
            context.TryGetEndpointCertificate(mount.Mount, out X509Certificate2? certificate, out X509Certificate2Collection chain);
            certificate?.Dispose();
            foreach (X509Certificate2 issuer in chain)
            {
                issuer.Dispose();
            }

            return input;
        }
        catch (Exception exception) when (exception is CryptographicException or InvalidOperationException or IOException or ArgumentException)
        {
            return ResourceMountInput.Unresolved(input.Source ?? string.Empty,
                $"Certificate for mount '{mount.Mount}' on resource '{plan.Resource}' is unusable: {exception.Message}");
        }
    }
}
