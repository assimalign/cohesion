using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.ApplicationModel;

internal static class ExternalBindingOverrides
{
    public static IExternalResourceResolver? FromCommandLine(
        ExternalResourceDeclaration declaration,
        IReadOnlyList<string> bindings)
    {
        ArgumentNullException.ThrowIfNull(declaration);
        ArgumentNullException.ThrowIfNull(bindings);

        var endpoints = new Dictionary<string, Uri>(StringComparer.Ordinal);
        IExternalResourceResolver? resolver = null;

        for (int index = 0; index < bindings.Count; index++)
        {
            if (!TryReadBinding(bindings[index], declaration.Name.ToString(), out string? remainder))
            {
                continue;
            }

            if (remainder.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            {
                EnsureSingleMode(declaration.Name, endpoints, resolver);
                resolver = new FileExternalResourceResolver(remainder["file:".Length..]);
                continue;
            }

            if (remainder.StartsWith("gateway:", StringComparison.OrdinalIgnoreCase))
            {
                EnsureSingleMode(declaration.Name, endpoints, resolver);
                resolver = new GatewayExternalResourceResolver(
                    new Uri(remainder["gateway:".Length..], UriKind.Absolute));
                continue;
            }

            if (resolver is not null)
            {
                throw new ArgumentException(
                    $"External '{declaration.Name}' mixes a static endpoint with a file or gateway binding.",
                    nameof(bindings));
            }

            ParseStaticBinding(declaration, remainder, endpoints);
        }

        return resolver ?? (endpoints.Count == 0 ? null : new StaticExternalResourceResolver(endpoints));
    }

    public static IExternalResourceResolver? FromEnvironment(ExternalResourceDeclaration declaration)
    {
        ArgumentNullException.ThrowIfNull(declaration);
        string name = declaration.Name.ToString();
        string? file = ReadEnvironment(name, "File");
        if (!string.IsNullOrWhiteSpace(file))
        {
            return new FileExternalResourceResolver(file);
        }

        string? gateway = ReadEnvironment(name, "Gateway");
        if (!string.IsNullOrWhiteSpace(gateway))
        {
            return new GatewayExternalResourceResolver(new Uri(gateway, UriKind.Absolute));
        }

        var endpoints = new Dictionary<string, Uri>(StringComparer.Ordinal);
        for (int index = 0; index < declaration.ReferencedEndpoints.Count; index++)
        {
            string endpoint = declaration.ReferencedEndpoints[index];
            string? url = ReadEnvironment(name, $"Endpoints__{endpoint}");
            if (!string.IsNullOrWhiteSpace(url))
            {
                endpoints.Add(endpoint, new Uri(url, UriKind.Absolute));
            }
        }

        return endpoints.Count == 0 ? null : new StaticExternalResourceResolver(endpoints);
    }

    private static void ParseStaticBinding(
        ExternalResourceDeclaration declaration,
        string value,
        IDictionary<string, Uri> endpoints)
    {
        string endpoint;
        string url;
        if (Uri.TryCreate(value, UriKind.Absolute, out Uri? shorthand))
        {
            endpoint = declaration.ReferencedEndpoints.Count switch
            {
                0 => shorthand.Scheme,
                1 => declaration.ReferencedEndpoints[0],
                _ => throw new ArgumentException(
                    $"External '{declaration.Name}' consumes multiple endpoints; use " +
                    $"--external {declaration.Name}=<endpoint>=<url>.",
                    nameof(value)),
            };
            url = value;
        }
        else
        {
            int separator = value.IndexOf('=');
            if (separator <= 0 || separator == value.Length - 1)
            {
                throw new ArgumentException(
                    $"External binding '{declaration.Name}={value}' must be a URL or '<endpoint>=<url>'.",
                    nameof(value));
            }

            endpoint = value[..separator];
            url = value[(separator + 1)..];
        }

        endpoints[endpoint] = new Uri(url, UriKind.Absolute);
    }

    private static bool TryReadBinding(string binding, string name, out string remainder)
    {
        int separator = binding.IndexOf('=');
        if (separator <= 0 || separator == binding.Length - 1 ||
            !binding.AsSpan(0, separator).Equals(name, StringComparison.OrdinalIgnoreCase))
        {
            remainder = string.Empty;
            return false;
        }

        remainder = binding[(separator + 1)..];
        return true;
    }

    private static string? ReadEnvironment(string name, string suffix)
    {
        string key = $"Cohesion__External__{name}__{suffix}";
        string? value = Environment.GetEnvironmentVariable(key);
        if (value is not null)
        {
            return value;
        }

        return Environment.GetEnvironmentVariable(key.Replace("__", ":", StringComparison.Ordinal));
    }

    private static void EnsureSingleMode(
        ResourceName name,
        IReadOnlyDictionary<string, Uri> endpoints,
        IExternalResourceResolver? resolver)
    {
        if (endpoints.Count != 0 || resolver is not null)
        {
            throw new ArgumentException(
                $"External '{name}' declares more than one non-static binding mode.");
        }
    }
}
