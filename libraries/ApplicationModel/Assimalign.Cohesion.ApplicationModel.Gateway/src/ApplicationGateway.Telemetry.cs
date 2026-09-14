using System;
using System.Collections.Generic;
using System.Text;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

public abstract partial class ApplicationGateway
{
    private readonly Dictionary<(ApplicationName Application, ResourceName Sink, ResourceName Emitter), string> _telemetryCredentials = new();

    private ResourceTelemetryInjection? ResolveTelemetry(IApplicationModel model, IApplicationResource emitter)
    {
        if (!TryGetOwnLogSpaceEndpoint(model, out ResourceManifest? sink, out Uri? endpoint) ||
            sink!.Name == emitter.Name)
        {
            return null;
        }
        lock (_credentialGate)
        {
            var key = (model.Name, sink.Name, emitter.Name);
            if (!_telemetryCredentials.TryGetValue(key, out string? credential))
            {
                credential = GetTrustState(model.Name).Issue(sink.Name.ToString(), emitter.Name.ToString(),
                    _options.BootstrapCredentialLifetime, _options.TimeProvider.GetUtcNow(), telemetry: true);
                _telemetryCredentials.Add(key, credential);
            }
            return new ResourceTelemetryInjection(endpoint!, Encoding.UTF8.GetBytes("Authorization: Bearer " + credential + "\n"));
        }
    }

    private bool TryGetOwnLogSpaceEndpoint(IApplicationModel model, out ResourceManifest? sink, out Uri? endpoint)
    {
        for (int index = 0; index < model.Manifests.Count; index++)
        {
            ResourceManifest candidate = model.Manifests[index];
            if (candidate.Application != model.Name || !string.Equals(candidate.Kind, "LogSpace", StringComparison.OrdinalIgnoreCase) ||
                IsExternalPlan(model.Descriptors[index].Plan!))
            {
                continue;
            }
            IApplicationResource resource = model.Descriptors[index].Resource;
            IApplicationResourceStateManager state = GetApplicationState(model);
            if (state.GetState(resource.Id) != ResourceLifecycle.Running) { continue; }
            foreach (ResourceEndpoint observed in state.GetObservedEndpoints(resource.Id))
            {
                if (observed.Name == "otlp" && string.Equals(observed.Scheme, "https", StringComparison.OrdinalIgnoreCase) &&
                    Uri.TryCreateEndpoint(observed.Scheme, observed.Host, observed.Port, null, out endpoint))
                {
                    sink = candidate;
                    return true;
                }
            }
            if (model.Environment.IsDevelopment)
            {
                foreach (ResourceManifestEndpoint declared in candidate.Endpoints)
                {
                    if (declared.Name == "otlp" && declared.Scheme == "https" && declared.DevPort is int port &&
                        Uri.TryCreateEndpoint(declared.Scheme, "127.0.0.1", port, null, out endpoint))
                    {
                        sink = candidate;
                        return true;
                    }
                }
            }
        }
        sink = null;
        endpoint = null;
        return false;
    }
}
