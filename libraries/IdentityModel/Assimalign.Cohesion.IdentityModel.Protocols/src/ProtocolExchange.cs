using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.IdentityModel.Protocols;

/// <summary>
/// Describes the two legs of one protocol exchange: where the request goes and where the
/// response comes back. Descriptive only — this library never executes an exchange.
/// </summary>
/// <remarks>
/// Each leg's transport shape is the <see cref="ProtocolBinding" /> of its endpoint. A
/// null <see cref="ResponseEndpoint" /> means the response returns on the same connection
/// (back-channel exchanges such as a token request). Front-channel exchanges terminate at
/// different endpoints in both protocols — an OpenID Connect authorization request goes to
/// the authorization endpoint while the response lands at the client's redirect URI, and a
/// SAML authentication request goes to the identity provider's single-sign-on endpoint
/// while the response lands at the service provider's assertion consumer endpoint. A SAML
/// artifact flow is two exchanges: the front-channel artifact delivery and the SOAP
/// artifact-resolution exchange.
/// </remarks>
public sealed class ProtocolExchange
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ProtocolExchange" /> class.
    /// </summary>
    /// <param name="requestEndpoint">The endpoint the request is sent to.</param>
    /// <param name="responseEndpoint">
    /// The endpoint the response is delivered to, or null when the response returns on the
    /// same connection (back-channel).
    /// </param>
    /// <param name="properties">Additional exchange detail. The dictionary is snapshotted; keys compare ordinally.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="requestEndpoint" /> is null.</exception>
    /// <exception cref="ArgumentException">
    /// Thrown when a property name is blank or a property value is undefined.
    /// </exception>
    public ProtocolExchange(
        ProtocolEndpoint requestEndpoint,
        ProtocolEndpoint? responseEndpoint = null,
        IReadOnlyDictionary<string, IdentityClaimValue>? properties = null)
    {
        ArgumentNullException.ThrowIfNull(requestEndpoint);

        RequestEndpoint = requestEndpoint;
        ResponseEndpoint = responseEndpoint;
        Properties = ModelSnapshot.Properties(properties, nameof(properties));
    }

    /// <summary>
    /// Gets the endpoint the request is sent to; its binding is the request leg's
    /// transport shape.
    /// </summary>
    public ProtocolEndpoint RequestEndpoint { get; }

    /// <summary>
    /// Gets the endpoint the response is delivered to, or null when the response returns
    /// on the same connection (back-channel). When present, its binding is the response
    /// leg's transport shape.
    /// </summary>
    public ProtocolEndpoint? ResponseEndpoint { get; }

    /// <summary>
    /// Gets additional exchange detail.
    /// </summary>
    public IReadOnlyDictionary<string, IdentityClaimValue> Properties { get; }

    /// <inheritdoc />
    public override string ToString()
        => ResponseEndpoint is null
            ? $"{RequestEndpoint.Location} ({RequestEndpoint.Binding}, back-channel)"
            : $"{RequestEndpoint.Location} ({RequestEndpoint.Binding}) -> {ResponseEndpoint.Location} ({ResponseEndpoint.Binding})";
}
