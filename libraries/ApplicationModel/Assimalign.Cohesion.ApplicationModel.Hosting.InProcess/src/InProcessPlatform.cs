using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;

namespace Assimalign.Cohesion.ApplicationModel.Hosting.InProcess;

/// <summary>
/// The in-process platform, on which a gateway runs its resources inside its own process: the
/// platform's name, and the endpoints at which the gateways of this process receive the instances
/// they run.
/// </summary>
/// <remarks>
/// <para>
/// An endpoint is an absolute URL whose scheme is the platform's name, such as
/// <c>inprocess://local/</c>. A gateway registers itself at its endpoint and names the endpoint to
/// every instance it starts (<see cref="AppEnvironment.Variables.ManifestEndpoint"/>). An instance's
/// client finds the gateway registered at the endpoint when each call is made, as an HTTP client
/// reaches whatever listens at its URL: a call while nothing is registered there fails as one to an
/// address nothing listens on does.
/// </para>
/// <para>
/// The endpoints belong to the process, so a gateway registers its own, unique one.
/// </para>
/// </remarks>
public static class InProcessPlatform
{
    private static readonly ConcurrentDictionary<string, IInProcessGateway> _gateways = new(StringComparer.Ordinal);

    /// <summary>
    /// Gets the platform's name, <c>inprocess</c>, which a gateway hands every instance it starts as
    /// their platform and which is the scheme of its endpoints.
    /// </summary>
    public static PlatformName Name { get; } = "inprocess";

    /// <summary>
    /// Registers a gateway at an endpoint inside this process: every call an instance's in-process
    /// client makes to the endpoint goes to <paramref name="gateway"/>, until the returned
    /// registration is disposed.
    /// </summary>
    /// <param name="endpoint">The endpoint, an absolute URL whose scheme is <see cref="Name"/>, such as <c>inprocess://local/</c>.</param>
    /// <param name="gateway">The gateway that receives what the instances send.</param>
    /// <returns>The registration; disposing it removes the endpoint.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="endpoint"/> is not an absolute in-process URL, or is already registered.
    /// </exception>
    public static IDisposable RegisterGateway(Uri endpoint, IInProcessGateway gateway)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(gateway);

        if (!IsEndpoint(endpoint))
        {
            throw new ArgumentException(ErrorMessages.EndpointInvalid, nameof(endpoint));
        }

        string key = endpoint.AbsoluteUri;
        if (!_gateways.TryAdd(key, gateway))
        {
            throw new ArgumentException(string.Format(CultureInfo.CurrentCulture, ErrorMessages.EndpointRegistered, key), nameof(endpoint));
        }

        return new Registration(key, gateway);
    }

    // Whether a URL is one of the platform's endpoints: absolute, with the platform's name as its scheme.
    internal static bool IsEndpoint(Uri endpoint)
    {
        return endpoint.IsAbsoluteUri && string.Equals(endpoint.Scheme, Name.Value, StringComparison.OrdinalIgnoreCase);
    }

    // The gateway registered at an endpoint when the call is made, if any.
    internal static IInProcessGateway? FindGateway(Uri endpoint)
    {
        return _gateways.TryGetValue(endpoint.AbsoluteUri, out IInProcessGateway? gateway) ? gateway : null;
    }

    private sealed class Registration : IDisposable
    {
        private readonly KeyValuePair<string, IInProcessGateway> _entry;

        public Registration(string key, IInProcessGateway gateway)
        {
            _entry = new KeyValuePair<string, IInProcessGateway>(key, gateway);
        }

        // Removes the endpoint only while it is still this registration's.
        public void Dispose()
        {
            _gateways.TryRemove(_entry);
        }
    }
}
