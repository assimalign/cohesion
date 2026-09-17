using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Selects how a <c>RemoteReference</c> resolves its external resource.
/// </summary>
public sealed class RemoteReferenceOptions
{
    private readonly Dictionary<string, Uri> _endpoints = new(StringComparer.Ordinal);
    private IExternalResourceResolver? _resolver;

    /// <summary>Binds the reference to a peer gateway's application control plane.</summary>
    /// <param name="url">The absolute peer control-plane URL.</param>
    /// <returns>These options.</returns>
    /// <exception cref="ArgumentException"><paramref name="url"/> is empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="url"/> is <see langword="null"/>.</exception>
    /// <exception cref="UriFormatException">
    /// <paramref name="url"/> is not a valid absolute URI.
    /// </exception>
    public RemoteReferenceOptions Gateway(string url)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        return Gateway(new Uri(url, UriKind.Absolute));
    }

    /// <summary>Binds the reference to a peer gateway's application control plane.</summary>
    /// <param name="url">The absolute peer control-plane URL.</param>
    /// <returns>These options.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="url"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="url"/> is not absolute.</exception>
    public RemoteReferenceOptions Gateway(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (!url.IsAbsoluteUri)
        {
            throw new ArgumentException("A peer gateway URL must be absolute.", nameof(url));
        }

        _endpoints.Clear();
        _resolver = new GatewayExternalResourceResolver(url);
        return this;
    }

    /// <summary>Adds or replaces a statically configured endpoint.</summary>
    /// <param name="name">The endpoint name.</param>
    /// <param name="url">The absolute endpoint URL.</param>
    /// <returns>These options.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="name"/> or <paramref name="url"/> is empty.
    /// </exception>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="name"/> or <paramref name="url"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="UriFormatException">
    /// <paramref name="url"/> is not a valid absolute URI.
    /// </exception>
    public RemoteReferenceOptions Endpoint(string name, string url)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        return Endpoint(name, new Uri(url, UriKind.Absolute));
    }

    /// <summary>Adds or replaces a statically configured endpoint.</summary>
    /// <param name="name">The endpoint name.</param>
    /// <param name="url">The absolute endpoint URL.</param>
    /// <returns>These options.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="name"/> is empty or <paramref name="url"/> is not absolute.
    /// </exception>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="name"/> or <paramref name="url"/> is <see langword="null"/>.
    /// </exception>
    public RemoteReferenceOptions Endpoint(string name, Uri url)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(url);
        if (!url.IsAbsoluteUri)
        {
            throw new ArgumentException("An external endpoint URL must be absolute.", nameof(url));
        }

        if (_resolver is not StaticExternalResourceResolver)
        {
            _endpoints.Clear();
        }

        _endpoints[name] = url;
        _resolver = new StaticExternalResourceResolver(_endpoints);
        return this;
    }

    /// <summary>Binds the reference to an application <c>export.json</c> document.</summary>
    /// <param name="path">The export document path.</param>
    /// <returns>These options.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>.</exception>
    public RemoteReferenceOptions File(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _endpoints.Clear();
        _resolver = new FileExternalResourceResolver(path);
        return this;
    }

    /// <summary>Binds a platform-contributed resolver, such as Kubernetes import.</summary>
    /// <param name="resolver">The resolver implementation.</param>
    /// <returns>These options.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="resolver"/> is <see langword="null"/>.
    /// </exception>
    public RemoteReferenceOptions Bind(IExternalResourceResolver resolver)
    {
        _endpoints.Clear();
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        return this;
    }

    internal IExternalResourceResolver? Resolver => _resolver;

    internal IReadOnlyList<string> EndpointNames
    {
        get
        {
            var names = new string[_endpoints.Count];
            int index = 0;
            foreach (string name in _endpoints.Keys)
            {
                names[index++] = name;
            }

            return names;
        }
    }
}
