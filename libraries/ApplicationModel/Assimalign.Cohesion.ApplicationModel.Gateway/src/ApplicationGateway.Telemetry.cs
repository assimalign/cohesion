using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.ApplicationModel.Gateway.Internal;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

public abstract partial class ApplicationGateway
{
    private readonly Dictionary<(ApplicationName Application, ResourceName Sink, ResourceName Emitter), ApplicationCredential> _telemetryCredentials = new();

    // Resolves the telemetry one emitter exports, from the sink the application registered in
    // ApplicationProviders.Telemetry. Without a registration no AppEnvironment.Variables telemetry
    // variable is injected; the gateway never discovers a sink by resource kind. The emitter is a
    // resource, or the gateway itself when it reports a management event.
    private async ValueTask<ResourceTelemetryInjection?> ResolveTelemetryAsync(
        IApplicationModel model,
        ResourceName emitter,
        CancellationToken cancellationToken)
    {
        ResourceTelemetrySink? sink = GetProviders(model).Telemetry;
        if (sink is null)
        {
            return null;
        }

        return sink.ExternalEndpoint is Uri external
            ? ResolveExternalTelemetry(model, external, sink.HeadersParameter)
            : await ResolveResourceTelemetryAsync(model, emitter, sink.Resource!.Value, sink.EndpointName!, cancellationToken)
                .ConfigureAwait(false);
    }

    // An external sink receives the configured address and, when a headers parameter is named,
    // that parameter's value as the export headers document. The gateway mints nothing for it.
    private ResourceTelemetryInjection ResolveExternalTelemetry(
        IApplicationModel model,
        Uri endpoint,
        string? headersParameter)
    {
        if (headersParameter is null)
        {
            return new ResourceTelemetryInjection(endpoint, ReadOnlyMemory<byte>.Empty);
        }

        if (!CanSendCredential(model, endpoint, out string? securityFailure))
        {
            throw new InvalidOperationException(securityFailure);
        }

        if (!GetParameters(model.Name).TryGetValue(headersParameter, out string? headers))
        {
            throw new InvalidOperationException(
                $"Telemetry headers parameter '{headersParameter}' for external sink '{endpoint}' is not " +
                $"bound for gateway '{Name}'. Bind the parameter, or register " +
                "ResourceTelemetrySink.External without a headers parameter.");
        }

        return new ResourceTelemetryInjection(endpoint, Encoding.UTF8.GetBytes(headers));
    }

    // A resource sink receives each emitter's own Telemetry credential: audience = sink, subject =
    // emitter, lifetime = BootstrapCredentialLifetime (the default issuer adds scope = telemetry),
    // cached per (application, sink, emitter) for the pass (reissued if it has expired) so it is
    // never the sink's bootstrap credential. The headers document names the credential's own scheme - the one carrier that
    // does - so a registered issuer may return a non-Bearer telemetry credential. The sink does not
    // export to itself, and an emitter reconciled before the sink is Running starts without
    // telemetry: no dependency is inferred.
    private async ValueTask<ResourceTelemetryInjection?> ResolveResourceTelemetryAsync(
        IApplicationModel model,
        ResourceName emitter,
        ResourceName sink,
        string endpointName,
        CancellationToken cancellationToken)
    {
        if (sink == emitter || !TryGetTelemetrySinkEndpoint(model, sink, endpointName, out Uri? endpoint))
        {
            return null;
        }

        var key = (model.Name, sink, emitter);
        DateTimeOffset now = _options.TimeProvider.GetUtcNow();
        ApplicationCredential? credential;
        lock (_credentialGate)
        {
            if (_telemetryCredentials.TryGetValue(key, out credential) && !IsUnexpired(credential, now))
            {
                credential = null;
            }
        }

        if (credential is null)
        {
            ApplicationCredential issued = await IssueCredentialAsync(
                    model,
                    sink.ToString(),
                    emitter.ToString(),
                    ApplicationCredentialPurpose.Telemetry,
                    _options.BootstrapCredentialLifetime,
                    cancellationToken)
                .ConfigureAwait(false);
            lock (_credentialGate)
            {
                if (!_telemetryCredentials.TryGetValue(key, out credential) || !IsUnexpired(credential, now))
                {
                    credential = issued;
                    _telemetryCredentials[key] = credential;
                }
            }
        }

        // Construction validates the single-line credential before it can enter the cache.
        // Transport deliberately unwraps the value; its string and debugger displays are redacted.
        return new ResourceTelemetryInjection(
            endpoint,
            Encoding.UTF8.GetBytes("Authorization: " + credential.Scheme.Value + " " + credential.Value.Value + "\n"));
    }

    // The sink's observed endpoint once it is Running (or, in Local, its declared DevPort), and
    // only over HTTPS: an emitter credential never travels in plaintext, not even to a Local
    // loopback sink. This is the transport rule the kind-discovered sink had; a registration
    // changes which sink is used, never how its credential travels.
    private bool TryGetTelemetrySinkEndpoint(
        IApplicationModel model,
        ResourceName sink,
        string endpointName,
        [NotNullWhen(true)] out Uri? endpoint)
    {
        for (int index = 0; index < model.Manifests.Count; index++)
        {
            ResourceManifest candidate = model.Manifests[index];
            if (candidate.Application != model.Name || candidate.Name != sink ||
                IsExternalPlan(model.Descriptors[index].Plan!))
            {
                continue;
            }

            IApplicationResource resource = model.Descriptors[index].Resource;
            IApplicationResourceStateManager state = GetApplicationState(model);
            if (state.GetState(resource.Id) != ResourceLifecycle.Running)
            {
                break;
            }

            foreach (ResourceEndpoint observed in state.GetObservedEndpoints(resource.Id))
            {
                if (observed.Name == endpointName &&
                    string.Equals(observed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
                    Uri.TryCreateEndpoint(observed.Scheme, observed.Host, observed.Port, null, out endpoint))
                {
                    return true;
                }
            }

            if (model.Environment.IsLocal)
            {
                foreach (ResourceManifestEndpoint declared in candidate.Endpoints)
                {
                    if (declared.Name == endpointName && declared.DevPort is int port &&
                        string.Equals(declared.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
                        Uri.TryCreateEndpoint(declared.Scheme, "127.0.0.1", port, null, out endpoint))
                    {
                        return true;
                    }
                }
            }

            break;
        }

        endpoint = null;
        return false;
    }
}
