using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using SecretStoreResourceCommand = Assimalign.Cohesion.SecretStore.Client.ResourceCommand;
using Shouldly;
using Xunit;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Hosting.Resources;
using Assimalign.Cohesion.SecretStore;
using Assimalign.Cohesion.SecretStore.Client;

namespace Assimalign.Cohesion.SecretStore.Hosting.Tests;

public sealed class CertificateEnrollmentEndToEndTests
{
    [Fact(DisplayName = "Cohesion Test [SecretStore.Hosting] - Certificate enrollment: two in-process stores exchange a trusted authenticated intermediate and issue a leaf")]
    public async Task Enrollment_BetweenInProcessStores_ShouldUseTrustGrantAndProducePlatformChain()
    {
        // Arrange
        using var platformDirectory = new TemporaryDirectory();
        using var childDirectory = new TemporaryDirectory();
        using var platformIdentity = new TestBootstrapIdentity("platform", "local");
        using var applicationIdentity = new TestBootstrapIdentity("appa", "local");
        using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Uri platformEndpoint = SecretStoreTestHost.GetEndpoint();
        Uri childEndpoint = SecretStoreTestHost.GetEndpoint();
        string platformToken = platformIdentity.Issue("platform-secrets");
        string applicationToPlatformToken = applicationIdentity.Issue("platform-secrets");
        string childToken = applicationIdentity.Issue("app-secrets");
        ResourceContext platformContext = SecretStoreTestHost.CreateContext(
            platformEndpoint,
            platformDirectory.Path,
            platformToken,
            platformIdentity.PublicKey,
            applicationName: "platform",
            resourceName: "platform-secrets");
        await using SecretStoreApplication platform = BuildApplication(
            platformContext,
            builder => builder.AddCertificateAuthority(options =>
                options.CommonName = "Cohesion Platform Root"));

        bool platformStarted = false;
        try
        {
            await ((IHost)platform).StartAsync(cancellationTokenSource.Token);
            platformStarted = true;
            ISecretStoreClient platformClient = SecretStoreClient.Create(
                platformEndpoint,
                new ClientCredential(platformToken));
            await platformClient.SendCommandAsync(
                new SecretStoreResourceCommand(
                    "trust-appa",
                    SecretsEndpointService.TrustAddCommand,
                    "platform@local",
                    applicationIdentity.Issuer,
                    applicationIdentity.PublicKey),
                cancellationTokenSource.Token);
            using (var httpClient = new HttpClient())
            using (HttpResponseMessage forbiddenPeerRead = await SendAsync(
                httpClient,
                HttpMethod.Get,
                new Uri(
                    platformEndpoint,
                    "/cohesion/v1/certificates?name=ca%2Froot"),
                applicationToPlatformToken,
                writeBody: null,
                cancellationTokenSource.Token))
            {
                forbiddenPeerRead.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            }

            string platformRootPem = await platformClient.GetCertificateAsync(
                "ca/root",
                cancellationTokenSource.Token);
            ResourceContext childContext = SecretStoreTestHost.CreateContext(
                childEndpoint,
                childDirectory.Path,
                childToken,
                applicationIdentity.PublicKey,
                applicationName: "appa",
                resourceName: "app-secrets");
            await using SecretStoreApplication child = BuildApplication(
                childContext,
                builder => builder.AddCertificateAuthority(options =>
                {
                    options.SelfSeedWhenNoPlatform = false;
                    options.PlatformEnrollmentEndpoint = new UriBuilder(platformEndpoint)
                    {
                        Scheme = Uri.UriSchemeHttps,
                    }.Uri;
                    options.PlatformCertificate = Encoding.UTF8.GetBytes(platformRootPem);
                }));

            bool childStarted = false;
            try
            {
                await ((IHost)child).StartAsync(cancellationTokenSource.Token);
                childStarted = true;
                using var httpClient = new HttpClient();
                ISecretStoreClient childClient = SecretStoreClient.Create(
                    childEndpoint,
                    new ClientCredential(childToken));
                await childClient.SendCommandAsync(
                    new SecretStoreResourceCommand(
                        "trust-platform",
                        SecretsEndpointService.TrustAddCommand,
                        "appa@local",
                        platformIdentity.Issuer,
                        platformIdentity.PublicKey),
                    cancellationTokenSource.Token);

                // Act
                using (HttpResponseMessage wrongIdentityResponse = await SendAsync(
                    httpClient,
                    HttpMethod.Get,
                    new Uri(
                        childEndpoint,
                        "/cohesion/v1/certificates/enrollment-request" +
                        "?application=appa&resource=another-store"),
                    childToken,
                    writeBody: null,
                    cancellationTokenSource.Token))
                {
                    wrongIdentityResponse.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
                }

                using HttpResponseMessage requestResponse = await SendAsync(
                    httpClient,
                    HttpMethod.Get,
                    new Uri(
                        childEndpoint,
                        "/cohesion/v1/certificates/enrollment-request" +
                        "?application=appa&resource=app-secrets"),
                    childToken,
                    writeBody: null,
                    cancellationTokenSource.Token);
                requestResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
                using JsonDocument requestDocument = JsonDocument.Parse(
                    await requestResponse.Content.ReadAsByteArrayAsync(cancellationTokenSource.Token));
                string certificateSigningRequest = requestDocument.RootElement
                    .GetProperty("certificateSigningRequest")
                    .GetString()!;

                using HttpResponseMessage issueResponse = await SendAsync(
                    httpClient,
                    HttpMethod.Post,
                    new Uri(platformEndpoint, "/cohesion/v1/certificates/enroll"),
                    applicationToPlatformToken,
                    writer =>
                    {
                        writer.WriteStartObject();
                        writer.WriteString("application", "appa");
                        writer.WriteString("resource", "app-secrets");
                        writer.WriteString("certificateSigningRequest", certificateSigningRequest);
                        writer.WriteEndObject();
                    },
                    cancellationTokenSource.Token);
                issueResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
                using JsonDocument issueDocument = JsonDocument.Parse(
                    await issueResponse.Content.ReadAsByteArrayAsync(cancellationTokenSource.Token));
                string certificate = issueDocument.RootElement.GetProperty("certificate").GetString()!;
                string[] issuerChain = issueDocument.RootElement
                    .GetProperty("issuerChain")
                    .EnumerateArray()
                    .Select(static item => item.GetString()!)
                    .ToArray();

                using HttpResponseMessage completionResponse = await SendAsync(
                    httpClient,
                    HttpMethod.Post,
                    new Uri(childEndpoint, "/cohesion/v1/certificates/enrollment"),
                    childToken,
                    writer =>
                    {
                        writer.WriteStartObject();
                        writer.WriteString("certificate", certificate);
                        writer.WritePropertyName("issuerChain");
                        writer.WriteStartArray();
                        for (int index = 0; index < issuerChain.Length; index++)
                        {
                            writer.WriteStringValue(issuerChain[index]);
                        }

                        writer.WriteEndArray();
                        writer.WriteEndObject();
                    },
                    cancellationTokenSource.Token);
                string leafPem = await childClient.GetCertificateAsync(
                    "certs/appa-api",
                    cancellationTokenSource.Token);

                // Assert
                completionResponse.StatusCode.ShouldBe(HttpStatusCode.NoContent);
                using X509Certificate2 platformRoot = X509Certificate2.CreateFromPem(platformRootPem);
                X509Certificate2Collection bundle = LoadCertificates(leafPem);
                try
                {
                    bundle.Count.ShouldBe(3);
                    X509Certificate2 leaf = bundle.Single(item => !IsCertificateAuthority(item));
                    X509Certificate2 intermediate = bundle.Single(item =>
                        IsCertificateAuthority(item) &&
                        !string.Equals(item.Subject, item.Issuer, StringComparison.Ordinal));
                    X509Certificate2 root = bundle.Single(item =>
                        IsCertificateAuthority(item) &&
                        string.Equals(item.Subject, item.Issuer, StringComparison.Ordinal));
                    root.RawData.ShouldBe(platformRoot.RawData);
                    BuildChain(leaf, root, [intermediate]).ShouldBeTrue();
                }
                finally
                {
                    DisposeCertificates(bundle);
                }
            }
            finally
            {
                if (childStarted)
                {
                    await ((IHost)child).StopAsync(cancellationTokenSource.Token);
                }
            }
        }
        finally
        {
            if (platformStarted)
            {
                await ((IHost)platform).StopAsync(cancellationTokenSource.Token);
            }
        }
    }

    private static SecretStoreApplication BuildApplication(
        ResourceContext context,
        Action<SecretStoreApplicationBuilder> configure)
    {
        using IDisposable scope = ResourceRuntime.CreateScope(context);
        SecretStoreApplicationBuilder builder = SecretStoreTestHost.CreateBuilder();
        configure.Invoke(builder);
        return builder.Build();
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        HttpMethod method,
        Uri requestUri,
        string token,
        Action<Utf8JsonWriter>? writeBody,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, requestUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (writeBody is not null)
        {
            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writeBody.Invoke(writer);
            }

            request.Content = new ByteArrayContent(buffer.WrittenSpan.ToArray());
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        return await client.SendAsync(request, cancellationToken);
    }

    private static bool BuildChain(
        X509Certificate2 leaf,
        X509Certificate2 root,
        X509Certificate2[] intermediates)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(root);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.DisableCertificateDownloads = true;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
        chain.ChainPolicy.VerificationTime = DateTime.UtcNow;
        chain.ChainPolicy.ExtraStore.AddRange(intermediates);
        return chain.Build(leaf);
    }

    private static bool IsCertificateAuthority(X509Certificate2 certificate)
        => certificate.Extensions
            .OfType<X509BasicConstraintsExtension>()
            .Single()
            .CertificateAuthority;

    private static X509Certificate2Collection LoadCertificates(string pem)
    {
        var certificates = new X509Certificate2Collection();
        certificates.ImportFromPem(pem);
        return certificates;
    }

    private static void DisposeCertificates(IEnumerable<X509Certificate2> certificates)
    {
        foreach (X509Certificate2 certificate in certificates)
        {
            certificate.Dispose();
        }
    }
}
