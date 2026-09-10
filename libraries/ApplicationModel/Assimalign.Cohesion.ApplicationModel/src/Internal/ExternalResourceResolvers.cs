using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel;

internal sealed class UnboundExternalResourceResolver : IExternalResourceResolver
{
    public static UnboundExternalResourceResolver Instance { get; } = new();

    private UnboundExternalResourceResolver()
    {
    }

    public ValueTask<ExternalResourceResolution> ResolveAsync(
        ExternalResourceResolutionContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(ExternalResourceResolution.Unresolved(
            $"External '{context.Declaration.Name}' has no code or environment binding."));
    }
}

internal sealed class StaticExternalResourceResolver : IExternalResourceResolver
{
    private readonly IReadOnlyDictionary<string, Uri> _endpoints;

    public StaticExternalResourceResolver(IReadOnlyDictionary<string, Uri> endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        _endpoints = new Dictionary<string, Uri>(endpoints, StringComparer.Ordinal);
    }

    internal IReadOnlyDictionary<string, Uri> Endpoints => _endpoints;

    public ValueTask<ExternalResourceResolution> ResolveAsync(
        ExternalResourceResolutionContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        var endpoints = new ResourceEndpoint[_endpoints.Count];
        int index = 0;
        foreach ((string name, Uri uri) in _endpoints)
        {
            endpoints[index++] = ExternalEndpointConverter.FromUri(name, uri);
        }

        return ValueTask.FromResult(new ExternalResourceResolution(true, endpoints));
    }
}

internal sealed class FileExternalResourceResolver : IExternalResourceResolver
{
    private readonly string _path;

    public FileExternalResourceResolver(string path)
    {
        _path = path;
    }

    internal string Path => _path;

    public async ValueTask<ExternalResourceResolution> ResolveAsync(
        ExternalResourceResolutionContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            ApplicationExportDocument export = await ApplicationExportDocument.LoadAsync(
                _path,
                cancellationToken).ConfigureAwait(false);
            return ExternalEndpointConverter.FromExport(export, context.Declaration);
        }
        catch (FileNotFoundException)
        {
            return ExternalResourceResolution.Unresolved(
                $"External '{context.Declaration.Name}' export file '{_path}' does not exist.");
        }
        catch (DirectoryNotFoundException)
        {
            return ExternalResourceResolution.Unresolved(
                $"External '{context.Declaration.Name}' export directory for '{_path}' does not exist.");
        }
    }
}

internal sealed class GatewayExternalResourceResolver : IExternalResourceResolver
{
    private readonly Uri _address;

    public GatewayExternalResourceResolver(Uri address)
    {
        _address = address;
    }

    internal Uri Address => _address;

    public async ValueTask<ExternalResourceResolution> ResolveAsync(
        ExternalResourceResolutionContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.ControlPlaneClient is null)
        {
            return ExternalResourceResolution.Unresolved(
                $"External '{context.Declaration.Name}' is bound to gateway '{_address}', " +
                "but no IControlPlaneClient is configured.");
        }

        ApplicationExportDocument export = await context.ControlPlaneClient.GetApplicationAsync(
            _address,
            cancellationToken).ConfigureAwait(false);
        return ExternalEndpointConverter.FromExport(export, context.Declaration);
    }
}

internal static class ExternalEndpointConverter
{
    public static ExternalResourceResolution FromExport(
        ApplicationExportDocument export,
        ExternalResourceDeclaration declaration)
    {
        ArgumentNullException.ThrowIfNull(export);
        ArgumentNullException.ThrowIfNull(declaration);

        ApplicationExportResource? resource = null;
        for (int index = 0; index < export.Resources.Count; index++)
        {
            if (string.Equals(
                    export.Resources[index].Name,
                    declaration.Name.ToString(),
                    StringComparison.Ordinal))
            {
                resource = export.Resources[index];
                break;
            }
        }

        if (resource is null)
        {
            return ExternalResourceResolution.Unresolved(
                $"Export for application '{export.Application}' does not contain external '{declaration.Name}'.");
        }

        ApplicationModelResourceDocument? modelResource = null;
        for (int index = 0; index < export.Model.Resources.Count; index++)
        {
            if (string.Equals(
                    export.Model.Resources[index].Name,
                    declaration.Name.ToString(),
                    StringComparison.Ordinal))
            {
                modelResource = export.Model.Resources[index];
                break;
            }
        }

        if (modelResource is null ||
            (declaration.Manifest is not null &&
             (!string.Equals(
                  export.Application,
                  declaration.Application.ToString(),
                  StringComparison.Ordinal) ||
              modelResource.Manifest.Application != declaration.Application)))
        {
            return ExternalResourceResolution.Unresolved(
                $"Export for application '{export.Application}' contains resource '{declaration.Name}', " +
                $"but its declared application is not '{declaration.Application}'.");
        }

        var endpoints = new ResourceEndpoint[resource.Endpoints.Count];
        for (int index = 0; index < endpoints.Length; index++)
        {
            ApplicationExportEndpoint endpoint = resource.Endpoints[index];
            string address = endpoint.Public ?? endpoint.Internal;
            endpoints[index] = FromUri(
                endpoint.Name,
                CreateEndpointUri(address, endpoint.Name, declaration));
        }

        return new ExternalResourceResolution(
            true,
            endpoints,
            resource.ManifestHash,
            export.SchemaVersion,
            $"Resolved from application export '{export.Application}'.");
    }

    public static ResourceEndpoint FromUri(string name, Uri uri)
    {
        int port = uri.IsDefaultPort
            ? uri.Scheme switch
            {
                "https" => 443,
                "http" => 80,
                _ => throw new InvalidOperationException(
                    $"External endpoint '{name}' URL '{uri}' must include an explicit port."),
            }
            : uri.Port;

        return new ResourceEndpoint(name, uri.Scheme, port, IsPublic: true, uri.Host);
    }

    private static Uri CreateEndpointUri(
        string address,
        string endpointName,
        ExternalResourceDeclaration declaration)
    {
        if (Uri.TryCreate(address, UriKind.Absolute, out Uri? uri))
        {
            return uri;
        }

        string scheme = "tcp";
        if (declaration.Manifest is ResourceManifest manifest)
        {
            for (int index = 0; index < manifest.Endpoints.Count; index++)
            {
                if (string.Equals(
                        manifest.Endpoints[index].Name,
                        endpointName,
                        StringComparison.Ordinal))
                {
                    scheme = manifest.Endpoints[index].Scheme;
                    break;
                }
            }
        }

        return new Uri($"{scheme}://{address}", UriKind.Absolute);
    }
}
