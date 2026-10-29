using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Hosting.Internal;

/// <summary>
/// Publishes to an endpoint inside the instance's own process: to the publisher registered for the
/// endpoint when each call is made, as an HTTP publisher reaches whatever listens on its URL.
/// </summary>
internal sealed class InProcessApplicationInstancePublisher : ApplicationInstancePublisher
{
    private static readonly ConcurrentDictionary<string, IApplicationInstancePublisher> _endpoints = new(StringComparer.Ordinal);

    private readonly Uri _endpoint;

    public InProcessApplicationInstancePublisher(Uri endpoint)
    {
        _endpoint = endpoint;
    }

    public override ValueTask<ApplicationInstancePublication> PublishAsync(ApplicationInstanceManifest manifest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        return Resolve().PublishAsync(manifest, cancellationToken);
    }

    public override ValueTask<bool> RenewAsync(ApplicationInstanceManifest manifest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        return Resolve().RenewAsync(manifest, cancellationToken);
    }

    public static bool IsInProcess(Uri endpoint)
    {
        return endpoint.IsAbsoluteUri && string.Equals(endpoint.Scheme, InProcessScheme, StringComparison.OrdinalIgnoreCase);
    }

    public static IDisposable Register(Uri endpoint, IApplicationInstancePublisher publisher)
    {
        string key = endpoint.AbsoluteUri;
        if (!_endpoints.TryAdd(key, publisher))
        {
            throw new ArgumentException(string.Format(CultureInfo.CurrentCulture, ErrorMessages.InProcessEndpointRegistered, key), nameof(endpoint));
        }

        return new Registration(key, publisher);
    }

    private IApplicationInstancePublisher Resolve()
    {
        return _endpoints.TryGetValue(_endpoint.AbsoluteUri, out IApplicationInstancePublisher? publisher)
            ? publisher
            : throw new InvalidOperationException(string.Format(CultureInfo.CurrentCulture, ErrorMessages.InProcessEndpointUnknown, _endpoint.AbsoluteUri));
    }

    private sealed class Registration : IDisposable
    {
        private readonly KeyValuePair<string, IApplicationInstancePublisher> _entry;

        public Registration(string key, IApplicationInstancePublisher publisher)
        {
            _entry = new KeyValuePair<string, IApplicationInstancePublisher>(key, publisher);
        }

        // Removes the endpoint only while it is still this registration's.
        public void Dispose()
        {
            _endpoints.TryRemove(_entry);
        }
    }
}
