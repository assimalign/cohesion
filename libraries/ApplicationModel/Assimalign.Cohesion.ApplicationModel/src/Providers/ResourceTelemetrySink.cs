using System;

namespace Assimalign.Cohesion.ApplicationModel;

// Deviates from the repo interface-first rule per the owner-approved BYO design (2026-09-25): an
// immutable value created only through its two factories; the gateway reads it, nothing implements it.

/// <summary>
/// Names the OTLP sink a gateway points every resource's telemetry at. Registered through
/// <see cref="ApplicationProviders.Telemetry"/>; without a registration the gateway injects no
/// telemetry settings.
/// </summary>
/// <remarks>
/// A sink is either a resource of the declaring application
/// (<see cref="FromResource(IApplicationResourceDescriptor, string)"/>, or
/// <see cref="FromResource(ResourceName, string)"/> for an application-set member) or an
/// endpoint outside the model (<see cref="External"/>). For a resource sink the gateway uses the
/// sink's observed endpoint once the sink is Running, only when that endpoint is HTTPS, and mints
/// each emitter a credential whose audience is the sink, whose subject is the emitter, and whose
/// scope is telemetry; the sink does not export to itself. For an external sink the gateway injects the address and, when
/// <see cref="HeadersParameter"/> names a parameter, that parameter's value as the export
/// headers.
/// </remarks>
public sealed class ResourceTelemetrySink
{
    private ResourceTelemetrySink(
        ResourceName? resource,
        string? endpointName,
        Uri? externalEndpoint,
        string? headersParameter)
    {
        Resource = resource;
        EndpointName = endpointName;
        ExternalEndpoint = externalEndpoint;
        HeadersParameter = headersParameter;
    }

    /// <summary>
    /// Gets the sink resource, or <see langword="null"/> for an external sink.
    /// </summary>
    public ResourceName? Resource { get; }

    /// <summary>
    /// Gets the name of the sink resource's OTLP endpoint, or <see langword="null"/> for an
    /// external sink.
    /// </summary>
    public string? EndpointName { get; }

    /// <summary>
    /// Gets the absolute OTLP address of an external sink, or <see langword="null"/> for a
    /// resource sink.
    /// </summary>
    public Uri? ExternalEndpoint { get; }

    /// <summary>
    /// Gets the name of the gateway parameter whose value is sent as the external sink's export
    /// headers, or <see langword="null"/> when no headers are sent. Always <see langword="null"/>
    /// for a resource sink, whose credential the gateway mints.
    /// </summary>
    public string? HeadersParameter { get; }

    /// <summary>
    /// Gets whether the sink lives outside the application model.
    /// </summary>
    public bool IsExternal => ExternalEndpoint is not null;

    /// <summary>
    /// Creates a sink backed by a resource of the declaring application.
    /// </summary>
    /// <param name="sink">The sink resource's descriptor.</param>
    /// <param name="endpoint">The name of the sink's OTLP endpoint.</param>
    /// <returns>The resource sink.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="sink"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="endpoint"/> is <see langword="null"/>, empty, or whitespace.</exception>
    public static ResourceTelemetrySink FromResource(IApplicationResourceDescriptor sink, string endpoint = "otlp")
    {
        ArgumentNullException.ThrowIfNull(sink);
        return FromResource(sink.Resource.Name, endpoint);
    }

    /// <summary>
    /// Creates a sink backed by a resource of the declaring application, named rather than passed as
    /// a descriptor: the form an application-set member's registration callback uses, where the
    /// member's resources come from its imported model.
    /// </summary>
    /// <param name="sink">The sink resource's name.</param>
    /// <param name="endpoint">The name of the sink's OTLP endpoint.</param>
    /// <returns>The resource sink.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="sink"/> or <paramref name="endpoint"/> is <see langword="null"/>, empty, or
    /// whitespace.
    /// </exception>
    /// <remarks>
    /// The sink must be a resource of the application the sink is registered for and declare
    /// <paramref name="endpoint"/>; provider validation checks both when the registrations are
    /// frozen (at <see cref="IApplicationBuilder.Build"/>, or when an application set binds a
    /// member's registrations).
    /// </remarks>
    public static ResourceTelemetrySink FromResource(ResourceName sink, string endpoint = "otlp")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sink.Value, nameof(sink));
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        return new ResourceTelemetrySink(sink, endpoint, externalEndpoint: null, headersParameter: null);
    }

    /// <summary>
    /// Creates a sink outside the application model.
    /// </summary>
    /// <param name="endpoint">The absolute <c>http</c> or <c>https</c> OTLP address.</param>
    /// <param name="headersParameter">
    /// The gateway parameter whose value is sent as the export headers, or <see langword="null"/>
    /// to send none.
    /// </param>
    /// <returns>The external sink.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="endpoint"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="endpoint"/> is not an absolute <c>http</c> or <c>https</c> address, or
    /// <paramref name="headersParameter"/> is empty or whitespace.
    /// </exception>
    public static ResourceTelemetrySink External(Uri endpoint, string? headersParameter = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!endpoint.IsAbsoluteUri ||
            (!string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal) &&
             !string.Equals(endpoint.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                $"Telemetry sink address '{endpoint}' must be an absolute http or https URI.",
                nameof(endpoint));
        }

        if (headersParameter is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(headersParameter);
        }

        return new ResourceTelemetrySink(resource: null, endpointName: null, endpoint, headersParameter);
    }
}
