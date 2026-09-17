using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;

namespace Assimalign.Cohesion.ApplicationModel;

internal static class ExternalResourceDocumentConverter
{
    public static ApplicationModelExternalDocument Create(ExternalResource resource)
    {
        ExternalResourceDeclaration declaration = resource.Declaration;
        string binding = "unbound";
        string? location = null;
        IReadOnlyList<ApplicationModelExternalEndpointDocument> endpoints =
            Array.Empty<ApplicationModelExternalEndpointDocument>();

        switch (resource.Resolver)
        {
            case StaticExternalResourceResolver staticResolver:
                binding = "static";
                var endpointDocuments = new List<ApplicationModelExternalEndpointDocument>(
                    staticResolver.Endpoints.Count);
                foreach ((string name, Uri url) in staticResolver.Endpoints)
                {
                    endpointDocuments.Add(new ApplicationModelExternalEndpointDocument(
                        name,
                        url.AbsoluteUri));
                }

                endpointDocuments.Sort(static (left, right) =>
                    StringComparer.Ordinal.Compare(left.Name, right.Name));
                endpoints = new ReadOnlyCollection<ApplicationModelExternalEndpointDocument>(
                    endpointDocuments);
                break;
            case FileExternalResourceResolver fileResolver:
                binding = "file";
                location = fileResolver.Path;
                break;
            case GatewayExternalResourceResolver gatewayResolver:
                binding = "gateway";
                location = gatewayResolver.Address.AbsoluteUri;
                break;
        }

        return new ApplicationModelExternalDocument(
            declaration.Application.ToString(),
            declaration.ReferencedEndpoints,
            declaration.Optional,
            declaration.Manifest is not null,
            declaration.Closure,
            resource.IsRealized,
            binding,
            location,
            endpoints);
    }

    public static ExternalResource Create(
        ResourceManifest manifest,
        ApplicationModelExternalDocument document)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(document);
        ApplicationName application = ApplicationName.Parse(document.Application);
        if (application != manifest.Application)
        {
            throw new InvalidDataException(
                $"External resource '{manifest.Name}' declares application '{application}', " +
                $"but its manifest declares '{manifest.Application}'.");
        }

        var declaration = new ExternalResourceDeclaration(
            manifest.Name,
            application,
            document.ReferencedEndpoints,
            document.Optional,
            document.HasEmbeddedManifest ? manifest : null,
            document.Closure);
        IExternalResourceResolver serializedResolver = CreateResolver(document);
        var resource = new ExternalResource(
            declaration,
            ExternalBindingOverrides.FromEnvironment(declaration) ?? serializedResolver);
        if (document.IsRealized)
        {
            if (declaration.Manifest is null)
            {
                throw new InvalidDataException(
                    $"Manifest-less external '{manifest.Name}' cannot be marked as realized.");
            }

            resource.Realize();
        }

        return resource;
    }

    private static IExternalResourceResolver CreateResolver(ApplicationModelExternalDocument document)
    {
        if (string.Equals(document.Binding, "unbound", StringComparison.Ordinal))
        {
            return UnboundExternalResourceResolver.Instance;
        }

        if (string.Equals(document.Binding, "file", StringComparison.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(document.Location))
            {
                throw new InvalidDataException("A file external binding must contain a path.");
            }

            return new FileExternalResourceResolver(document.Location);
        }

        if (string.Equals(document.Binding, "gateway", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(document.Location, UriKind.Absolute, out Uri? address))
            {
                throw new InvalidDataException("A gateway external binding must contain an absolute URL.");
            }

            return new GatewayExternalResourceResolver(address);
        }

        if (!string.Equals(document.Binding, "static", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"External binding kind '{document.Binding}' is not supported.");
        }

        var endpoints = new Dictionary<string, Uri>(StringComparer.Ordinal);
        for (int index = 0; index < document.Endpoints.Count; index++)
        {
            ApplicationModelExternalEndpointDocument endpoint = document.Endpoints[index];
            if (!Uri.TryCreate(endpoint.Url, UriKind.Absolute, out Uri? address))
            {
                throw new InvalidDataException(
                    $"External endpoint '{endpoint.Name}' does not contain an absolute URL.");
            }

            if (!endpoints.TryAdd(endpoint.Name, address))
            {
                throw new InvalidDataException(
                    $"External binding contains duplicate endpoint '{endpoint.Name}'.");
            }
        }

        return new StaticExternalResourceResolver(endpoints);
    }
}
