using System;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace Assimalign.Cohesion.Connections.Security;

/// <summary>
/// Options that control a server-side TLS connection upgrade.
/// </summary>
public sealed class TlsServerOptions
{
    // The validation callback RequireClientCertificate/AllowClientCertificate installed, so a later
    // call can replace its own callback but never silently overwrite one the application set.
    private RemoteCertificateValidationCallback? _clientCertificateValidator;
    private int _maxConcurrentHandshakes = 512;

    /// <summary>
    /// Gets or sets the underlying TLS server authentication options (server certificate, enabled
    /// protocols, ALPN application protocols, client-certificate policy, and so on).
    /// </summary>
    public SslServerAuthenticationOptions AuthenticationOptions { get; set; } = new();

    /// <summary>
    /// Gets or sets the maximum time allowed for the TLS handshake to complete.
    /// </summary>
    /// <remarks>
    /// Defaults to 10 seconds. A non-positive value disables the timeout. The timeout applies to each
    /// connection's handshake on its own: a handshake that exceeds it fails, and on a TLS-layered listener
    /// that closes only that connection. With the timeout disabled, a client that never finishes its
    /// handshake holds one of the listener's <see cref="MaxConcurrentHandshakes"/> until it disconnects.
    /// </remarks>
    public TimeSpan HandshakeTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Gets or sets the most connections a TLS-layered listener holds at once while their handshakes run.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Defaults to 512. A listener composed with <c>UseTls</c> runs each accepted connection's handshake
    /// on its own task, so a slow or silent client delays only itself. A connection counts against this
    /// limit from the moment the listener accepts it from the transport until <c>AcceptAsync</c> returns
    /// it secured, or its handshake fails or exceeds <see cref="HandshakeTimeout"/>. At the limit the
    /// listener stops accepting from the transport and further clients wait in the transport's own
    /// backlog (for TCP, the operating system's listen queue), so a flood of stalled handshakes holds at
    /// most this many connections instead of growing without bound.
    /// </para>
    /// <para>
    /// The limit trades memory for availability: while it is reached, a new client waits until a slot
    /// frees, which a stalled client does at the latest after <see cref="HandshakeTimeout"/>. Raise it for
    /// an endpoint that expects many slow handshakes at once, or shorten the timeout. The value is read
    /// when the listener is composed. It does not apply to a QUIC listener given these
    /// <see cref="AuthenticationOptions"/>: a QUIC listener bounds its pending handshakes with its own
    /// backlog.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the assigned value is less than 1.</exception>
    public int MaxConcurrentHandshakes
    {
        get => _maxConcurrentHandshakes;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            _maxConcurrentHandshakes = value;
        }
    }

    /// <summary>
    /// Requests a client certificate in the handshake and refuses a client that presents none or one
    /// that fails validation (mutual TLS).
    /// </summary>
    /// <param name="validate">
    /// Decides whether a presented certificate is accepted. It receives the certificate, the chain the
    /// platform built for it, and the platform's verdict; return <see langword="true"/> to accept. When
    /// <see langword="null"/>, a certificate is accepted only when the platform reports no policy error,
    /// which means it chains to a root the machine trusts.
    /// </param>
    /// <returns>The current options instance.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <see cref="AuthenticationOptions"/> already carries a
    /// <see cref="SslServerAuthenticationOptions.RemoteCertificateValidationCallback"/> these methods did
    /// not install.
    /// </exception>
    /// <remarks>See <see cref="AllowClientCertificate"/> for how the policy is applied.</remarks>
    public TlsServerOptions RequireClientCertificate(Func<X509Certificate2, X509Chain?, SslPolicyErrors, bool>? validate = null)
    {
        return UseClientCertificatePolicy(required: true, validate);
    }

    /// <summary>
    /// Requests a client certificate in the handshake but completes the handshake without one; a
    /// certificate the client does present must pass validation.
    /// </summary>
    /// <param name="validate">
    /// Decides whether a presented certificate is accepted. It receives the certificate, the chain the
    /// platform built for it, and the platform's verdict; return <see langword="true"/> to accept. When
    /// <see langword="null"/>, a certificate is accepted only when the platform reports no policy error,
    /// which means it chains to a root the machine trusts.
    /// </param>
    /// <returns>The current options instance.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <see cref="AuthenticationOptions"/> already carries a
    /// <see cref="SslServerAuthenticationOptions.RemoteCertificateValidationCallback"/> these methods did
    /// not install.
    /// </exception>
    /// <remarks>
    /// <para>
    /// Both methods write the policy into the current <see cref="AuthenticationOptions"/>: they set
    /// <see cref="SslServerAuthenticationOptions.ClientCertificateRequired"/>, which makes the server
    /// send a <c>CertificateRequest</c> during the handshake (RFC 8446 §4.3.2), and install a
    /// <see cref="SslServerAuthenticationOptions.RemoteCertificateValidationCallback"/> that applies
    /// the policy. Every consumer of those authentication options applies it: the TLS layer for TCP
    /// listeners and a QUIC listener that is given the same options. Assign a replacement
    /// <see cref="AuthenticationOptions"/> before calling them, not after.
    /// </para>
    /// <para>
    /// The certificate is requested during the handshake and never afterwards: HTTP/2 forbids TLS 1.3
    /// post-handshake authentication (RFC 9113 §9.2.3) and TLS 1.2 renegotiation (§9.2.1), and the
    /// policy is the same on every protocol the listener serves. Calling either method again replaces
    /// the earlier policy.
    /// </para>
    /// </remarks>
    public TlsServerOptions AllowClientCertificate(Func<X509Certificate2, X509Chain?, SslPolicyErrors, bool>? validate = null)
    {
        return UseClientCertificatePolicy(required: false, validate);
    }

    private TlsServerOptions UseClientCertificatePolicy(bool required, Func<X509Certificate2, X509Chain?, SslPolicyErrors, bool>? validate)
    {
        SslServerAuthenticationOptions authentication = AuthenticationOptions;
        RemoteCertificateValidationCallback? current = authentication.RemoteCertificateValidationCallback;

        if (current is not null && !ReferenceEquals(current, _clientCertificateValidator))
        {
            throw new InvalidOperationException(
                "The authentication options already carry a RemoteCertificateValidationCallback. Configure the " +
                "client-certificate policy either through RequireClientCertificate/AllowClientCertificate or " +
                "through AuthenticationOptions, not both.");
        }

        RemoteCertificateValidationCallback validator = (_, certificate, chain, errors) =>
            ValidateClientCertificate(required, validate, certificate, chain, errors);

        authentication.ClientCertificateRequired = true;
        authentication.RemoteCertificateValidationCallback = validator;
        _clientCertificateValidator = validator;

        return this;
    }

    private static bool ValidateClientCertificate(
        bool required,
        Func<X509Certificate2, X509Chain?, SslPolicyErrors, bool>? validate,
        X509Certificate? certificate,
        X509Chain? chain,
        SslPolicyErrors errors)
    {
        if (certificate is null)
        {
            // The client presented no certificate (RemoteCertificateNotAvailable).
            return !required;
        }

        if (validate is null)
        {
            return errors == SslPolicyErrors.None;
        }

        // The platform stacks hand the callback an X509Certificate2; anything else is copied for the
        // duration of the call.
        if (certificate is X509Certificate2 certificate2)
        {
            return validate(certificate2, chain, errors);
        }

        using X509Certificate2 copy = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
        return validate(copy, chain, errors);
    }
}
