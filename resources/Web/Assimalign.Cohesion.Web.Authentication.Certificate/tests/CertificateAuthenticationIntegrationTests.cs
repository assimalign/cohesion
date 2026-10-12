using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Net.Http;
using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Connections.Security;
using Assimalign.Cohesion.Testing;
using Assimalign.Cohesion.Web.Authentication.Certificate.Internal;
using Assimalign.Cohesion.Web.Authentication.Certificate.Tests.TestObjects;

namespace Assimalign.Cohesion.Web.Authentication.Certificate.Tests;

[Collection(nameof(CertificateEventSourceCollection))]
public class CertificateAuthenticationIntegrationTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(30);

    public static IEnumerable<object[]> HttpVersions => [[System.Net.HttpVersion.Version11], [System.Net.HttpVersion.Version20]];

    [Theory(DisplayName = "Cohesion Test [Web.Authentication.Certificate] - TLS: Accepted certificate should authenticate with certificate claims")]
    [MemberData(nameof(HttpVersions))]
    public async Task Authenticate_ValidatedTlsCertificate_ShouldAuthenticatePrincipal(Version version)
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_timeout);
        using X509Certificate2 serverCertificate = TestCertificates.CreateServer();
        using X509Certificate2 clientCertificate = TestCertificates.CreateClient();
        TlsServerOptions tls = CreateOptions(serverCertificate).AllowClientCertificate((presented, _, _) => presented.Thumbprint == clientCertificate.Thumbprint);
        await using CertificateTestServer server = await CertificateTestServer.StartAsync(tls, cancellation.Token);

        // Act
        using HttpResponseMessage response = await SendAsync(server, "/secured", version, clientCertificate, cancellation.Token);

        // Assert
        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
        response.Version.ShouldBe(version);
        (await response.Content.ReadAsStringAsync(cancellation.Token)).ShouldBe(clientCertificate.Subject);
        ClaimsPrincipal principal = await server.Observed.Task.WaitAsync(cancellation.Token);
        principal.Identity.ShouldNotBeNull().IsAuthenticated.ShouldBeTrue();
        principal.FindFirst(ClaimTypes.X500DistinguishedName).ShouldNotBeNull().Value.ShouldBe(clientCertificate.Subject);
        principal.FindFirst(CertificateClaimTypes.Issuer).ShouldNotBeNull().Value.ShouldBe(clientCertificate.Issuer);
        principal.FindFirst(ClaimTypes.Thumbprint).ShouldNotBeNull().Value.ShouldBe(clientCertificate.Thumbprint);
        principal.FindFirst(ClaimTypes.SerialNumber).ShouldNotBeNull().Value.ShouldBe(clientCertificate.SerialNumber);
    }

    [Theory(DisplayName = "Cohesion Test [Web.Authentication.Certificate] - TLS: Optional certificate absence should be anonymous and challenge protected routes")]
    [MemberData(nameof(HttpVersions))]
    public async Task Authenticate_OptionalTlsCertificateAbsent_ShouldRemainAnonymous(Version version)
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_timeout);
        using X509Certificate2 serverCertificate = TestCertificates.CreateServer();
        TlsServerOptions tls = CreateOptions(serverCertificate).AllowClientCertificate(static (_, _, _) => true);
        await using CertificateTestServer server = await CertificateTestServer.StartAsync(tls, cancellation.Token);

        // Act
        using HttpResponseMessage publicResponse = await SendAsync(server, "/public", version, null, cancellation.Token);
        using HttpResponseMessage securedResponse = await SendAsync(server, "/secured", version, null, cancellation.Token);

        // Assert
        publicResponse.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
        (await publicResponse.Content.ReadAsStringAsync(cancellation.Token)).ShouldBe("anonymous");
        (await server.Observed.Task.WaitAsync(cancellation.Token)).Identity.ShouldNotBeNull().IsAuthenticated.ShouldBeFalse();
        securedResponse.StatusCode.ShouldBe(System.Net.HttpStatusCode.Unauthorized);
        securedResponse.Headers.WwwAuthenticate.ShouldHaveSingleItem().Scheme.ShouldBe("Certificate");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authentication.Certificate] - Authorization: A named certificate policy should ignore the authenticated default scheme")]
    public async Task RequireAuthorization_CertificateSchemeWithDifferentDefault_ShouldRequireCertificate()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_timeout);
        using X509Certificate2 serverCertificate = TestCertificates.CreateServer();
        using X509Certificate2 clientCertificate = TestCertificates.CreateClient();
        TlsServerOptions tls = CreateOptions(serverCertificate).AllowClientCertificate((presented, _, _) => presented.Thumbprint == clientCertificate.Thumbprint);
        await using CertificateTestServer server = await CertificateTestServer.StartAsync(tls, cancellation.Token, alternateDefault: true);

        // Act
        using HttpResponseMessage absent = await SendAsync(server, "/secured", System.Net.HttpVersion.Version20, null, cancellation.Token);
        using HttpResponseMessage present = await SendAsync(server, "/secured", System.Net.HttpVersion.Version20, clientCertificate, cancellation.Token);

        // Assert
        absent.Headers.GetValues("X-Default-User").ShouldHaveSingleItem().ShouldBe("alternate");
        absent.StatusCode.ShouldBe(System.Net.HttpStatusCode.Unauthorized);
        absent.Headers.WwwAuthenticate.ShouldHaveSingleItem().Scheme.ShouldBe("Certificate");
        present.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
        (await present.Content.ReadAsStringAsync(cancellation.Token)).ShouldBe(clientCertificate.Subject);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authentication.Certificate] - Authorization: Missing role should forbid the authenticated certificate")]
    public async Task RequireAuthorization_CertificateWithoutRequiredRole_ShouldForbid()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_timeout);
        using X509Certificate2 serverCertificate = TestCertificates.CreateServer();
        using X509Certificate2 clientCertificate = TestCertificates.CreateClient();
        TlsServerOptions tls = CreateOptions(serverCertificate).RequireClientCertificate((presented, _, _) => presented.Thumbprint == clientCertificate.Thumbprint);
        await using CertificateTestServer server = await CertificateTestServer.StartAsync(tls, cancellation.Token, requiredRole: "admin");

        // Act
        using HttpResponseMessage response = await SendAsync(server, "/secured", System.Net.HttpVersion.Version11, clientCertificate, cancellation.Token);

        // Assert
        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.Forbidden);
        response.Headers.WwwAuthenticate.ShouldBeEmpty();
        server.Observed.Task.IsCompleted.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authentication.Certificate] - Authorization: Application certificate rejection should challenge")]
    public async Task Authenticate_ApplicationRejectsCertificate_ShouldChallenge()
    {
        // Arrange
        using EventSourceRecorder recorder = new(CertificateAuthenticationEventSource.Log, EventLevel.Verbose);
        long before = CertificateAuthenticationEventSource.Log.RejectedCertificates;
        string requestId = ActivityTraceId.CreateRandom().ToHexString();
        int callbacks = 0;
        using CancellationTokenSource cancellation = new(_timeout);
        using X509Certificate2 serverCertificate = TestCertificates.CreateServer();
        using X509Certificate2 clientCertificate = TestCertificates.CreateClient();
        TlsServerOptions tls = CreateOptions(serverCertificate).RequireClientCertificate((presented, _, _) => presented.Thumbprint == clientCertificate.Thumbprint);
        await using CertificateTestServer server = await CertificateTestServer.StartAsync(tls, cancellation.Token, options =>
        {
            options.OnCertificateValidated = (context, _) =>
            {
                Interlocked.Increment(ref callbacks);
                context.Fail("account disabled");
                return ValueTask.CompletedTask;
            };
        });

        // Act
        using HttpResponseMessage response = await SendAsync(server, "/secured", System.Net.HttpVersion.Version20, clientCertificate, cancellation.Token, requestId);

        // Assert
        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.ShouldHaveSingleItem().Scheme.ShouldBe("Certificate");
        server.Observed.Task.IsCompleted.ShouldBeFalse();
        callbacks.ShouldBe(1, "UseAuthentication and the named Authorization policy share the handler's cached result.");
        CertificateAuthenticationEventSource.Log.RejectedCertificates.ShouldBe(before + 1);
        recorder.Events.ShouldNotContain(item => item.EventId == 0);
        EventWrittenEventArgs rejection = recorder.Events.Where(item => item.EventId == 1 && item.Payload is { Count: 3 } && Equals(item.Payload[2], requestId)).ShouldHaveSingleItem();
        rejection.Payload![0].ShouldBe(CertificateAuthenticationDefaults.AuthenticationScheme);
        rejection.Payload[1].ShouldBe("ValidationFailed");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authentication.Certificate] - TLS: Required certificate absence should fail before HTTP authentication")]
    public async Task Authenticate_RequiredCertificateAbsent_ShouldRejectHandshake()
    {
        // Arrange
        using EventSourceRecorder recorder = new(CertificateAuthenticationEventSource.Log, EventLevel.Verbose);
        long before = CertificateAuthenticationEventSource.Log.RejectedCertificates;
        using CancellationTokenSource cancellation = new(_timeout);
        using X509Certificate2 serverCertificate = TestCertificates.CreateServer();
        TlsServerOptions tls = CreateOptions(serverCertificate).RequireClientCertificate(static (_, _, _) => true);
        await using CertificateTestServer server = await CertificateTestServer.StartAsync(tls, cancellation.Token);

        // Act / Assert
        await Should.ThrowAsync<HttpRequestException>(() => SendAsync(server, "/public", System.Net.HttpVersion.Version11, null, cancellation.Token));
        server.Requests.ShouldBe(0);
        CertificateAuthenticationEventSource.Log.RejectedCertificates.ShouldBe(before);
        recorder.Events.ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Web.Authentication.Certificate] - TLS: Untrusted certificate should fail before HTTP authentication")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Authenticate_UntrustedCertificate_ShouldRejectHandshake(bool requireCertificate)
    {
        // Arrange
        using EventSourceRecorder recorder = new(CertificateAuthenticationEventSource.Log, EventLevel.Verbose);
        long before = CertificateAuthenticationEventSource.Log.RejectedCertificates;
        using CancellationTokenSource cancellation = new(_timeout);
        using X509Certificate2 serverCertificate = TestCertificates.CreateServer();
        using X509Certificate2 untrusted = TestCertificates.CreateClient();
        TlsServerOptions tls = CreateOptions(serverCertificate);
        // Without a validation callback the endpoint uses platform trust; this certificate is self-signed.
        if (requireCertificate)
        {
            tls.RequireClientCertificate();
        }
        else
        {
            tls.AllowClientCertificate();
        }
        await using CertificateTestServer server = await CertificateTestServer.StartAsync(tls, cancellation.Token);

        // Act / Assert
        await Should.ThrowAsync<HttpRequestException>(() => SendAsync(server, "/public", System.Net.HttpVersion.Version20, untrusted, cancellation.Token));
        server.Requests.ShouldBe(0);
        CertificateAuthenticationEventSource.Log.RejectedCertificates.ShouldBe(before);
        recorder.Events.ShouldBeEmpty();
    }

    private static TlsServerOptions CreateOptions(X509Certificate2 certificate) => new() { AuthenticationOptions = { ServerCertificate = certificate } };

    private static async Task<HttpResponseMessage> SendAsync(CertificateTestServer server, string path, Version version, X509Certificate2? certificate, CancellationToken cancellationToken, string? requestId = null)
    {
        SocketsHttpHandler transport = new()
        {
            SslOptions = { RemoteCertificateValidationCallback = static (_, _, _, _) => true },
        };
        if (certificate is not null)
        {
            transport.SslOptions.ClientCertificates = new X509CertificateCollection { certificate };
            transport.SslOptions.LocalCertificateSelectionCallback = (_, _, _, _, _) => certificate;
        }

        using HttpClient client = new(transport, disposeHandler: true);
        using HttpRequestMessage request = new(HttpMethod.Get, new Uri($"https://127.0.0.1:{server.Port}{path}"))
        {
            Version = version,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        if (requestId is not null)
        {
            request.Headers.TryAddWithoutValidation("traceparent", $"00-{requestId}-0123456789abcdef-00");
        }

        return await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
