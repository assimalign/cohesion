using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Cli.Tests;

/// <summary>Exercises OIDC device authorization with deterministic HTTP and polling time.</summary>
public sealed class DeviceLoginTests
{
    private const string issuer = "http://localhost:5020";
    private const string discovery = """
        {"issuer":"http://localhost:5020","device_authorization_endpoint":"http://localhost:5020/oauth2/device_authorization",
        "token_endpoint":"http://localhost:5020/oauth2/token"}
        """;
    private const string authorization = """
        {"device_code":"synthetic-device","user_code":"TEST-CODE","verification_uri":"http://localhost:5020/oauth2/device",
        "verification_uri_complete":"http://localhost:5020/oauth2/device?user_code=TEST-CODE","expires_in":600,"interval":1}
        """;
    private const string success = """
        {"access_token":"synthetic-access-token","token_type":"Bearer","expires_in":3600}
        """;

    /// <summary>Discovery without device flow is an explicit, actionable stub boundary.</summary>
    [Fact(DisplayName = "Cohesion Test [Tooling.Cli] - Login: Should reject missing device flow")]
    public async Task Login_WithoutDeviceEndpoint_ShouldReturnTwoAsync()
    {
        int calls = 0;
        using var fixture = new CliFixture((request, _) =>
        {
            calls++;
            request.Method.ShouldBe(HttpMethod.Get);
            request.RequestUri!.AbsoluteUri.ShouldBe(issuer + "/.well-known/openid-configuration");
            return Task.FromResult(CliFixture.Json("{\"token_endpoint\":\"http://localhost:5020/oauth2/token\"}"));
        });

        (await fixture.Application().ExecuteAsync(["login", "--issuer", issuer, "--client-id", "public"], CancellationToken.None)).ShouldBe(2);

        calls.ShouldBe(1);
        fixture.Error.ToString().ShouldContain("does not expose the device flow", Case.Sensitive);
        fixture.Error.ToString().ShouldContain("Development hub bound to loopback", Case.Sensitive);
        Directory.Exists(fixture.Home).ShouldBeFalse();
        fixture.Runner.Calls.ShouldBeEmpty();
    }

    /// <summary>Forms, polling backoff and the proposed protected credential shape match the protocol.</summary>
    [Fact(DisplayName = "Cohesion Test [Tooling.Cli] - Login: Should honor pending and slow_down then store protected credentials")]
    public async Task Login_WithApproval_ShouldStoreProtectedCredentialAsync()
    {
        int polls = 0;
        using var fixture = new CliFixture(async (request, token) =>
        {
            if (request.Method == HttpMethod.Get)
            {
                return CliFixture.Json(discovery);
            }
            request.Method.ShouldBe(HttpMethod.Post);
            request.Content!.Headers.ContentType!.MediaType.ShouldBe("application/x-www-form-urlencoded");
            string form = await request.Content.ReadAsStringAsync(token);
            form.ShouldContain("client_id=client+id", Case.Sensitive);
            form.ShouldContain("client_secret=synthetic%2Bsecret", Case.Sensitive);
            if (request.RequestUri!.AbsolutePath == "/oauth2/device_authorization")
            {
                form.ShouldContain("audience=cohesion-gateway", Case.Sensitive);
                form.ShouldContain("scope=openid+profile", Case.Sensitive);
                return CliFixture.Json(authorization);
            }
            request.RequestUri.AbsoluteUri.ShouldBe(issuer + "/oauth2/token");
            form.ShouldContain("grant_type=urn%3Aietf%3Aparams%3Aoauth%3Agrant-type%3Adevice_code", Case.Sensitive);
            form.ShouldContain("device_code=synthetic-device", Case.Sensitive);
            polls++;
            return polls switch
            {
                1 => CliFixture.Json("{\"error\":\"authorization_pending\"}", HttpStatusCode.BadRequest),
                2 => CliFixture.Json("{\"error\":\"slow_down\"}", HttpStatusCode.BadRequest),
                _ => CliFixture.Json(success)
            };
        });

        int result = await fixture.Application().ExecuteAsync(
            ["login", "--issuer", issuer, "--client-id", "client id", "--client-secret", "synthetic+secret",
                "--audience", "cohesion-gateway", "--scope", "openid profile"], CancellationToken.None);

        result.ShouldBe(0);
        fixture.Delays.ShouldBe(new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(6) });
        string path = Path.Combine(fixture.Home, ".cohesion", "credentials", "localhost.json");
        using JsonDocument document = JsonDocument.Parse(ProtectedFile.Read(path));
        document.RootElement.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal)
            .ShouldBe(new[] { "access_token", "expires_at", "issuer", "token_type" });
        document.RootElement.GetProperty("access_token").GetString().ShouldBe("synthetic-access-token");
        document.RootElement.GetProperty("token_type").GetString().ShouldBe("Bearer");
        document.RootElement.GetProperty("issuer").GetString().ShouldBe(issuer);
        document.RootElement.GetProperty("expires_at").GetDateTimeOffset().ShouldBe(fixture.Clock.GetUtcNow().AddHours(1));
        document.RootElement.EnumerateObject().ShouldNotContain(property => property.Name == "client_secret");
        if (!OperatingSystem.IsWindows())
        {
            File.GetUnixFileMode(path).ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        fixture.Error.ToString().ShouldContain("http://localhost:5020/oauth2/device?user_code=TEST-CODE", Case.Sensitive);
        fixture.Error.ToString().ShouldContain("TEST-CODE", Case.Sensitive);
        fixture.Output.ToString().ShouldNotContain("synthetic-access-token", Case.Sensitive);
        fixture.Error.ToString().ShouldNotContain("synthetic+secret", Case.Sensitive);
        fixture.Runner.Calls.ShouldBeEmpty();
    }

    /// <summary>Public-client printing does not create a credential store and stdout contains only the token.</summary>
    [Fact(DisplayName = "Cohesion Test [Tooling.Cli] - Login: Should print only token and preserve default openid scope")]
    public async Task Login_WithPrint_ShouldAvoidPersistenceAsync()
    {
        using var fixture = new CliFixture(async (request, token) =>
        {
            if (request.Method == HttpMethod.Get)
            {
                return CliFixture.Json(discovery);
            }
            string form = await request.Content!.ReadAsStringAsync(token);
            form.ShouldNotContain("client_secret", Case.Sensitive);
            if (request.RequestUri!.AbsolutePath == "/oauth2/device_authorization")
            {
                form.ShouldContain("scope=openid", Case.Sensitive);
                form.ShouldNotContain("audience", Case.Sensitive);
                return CliFixture.Json(authorization);
            }
            return CliFixture.Json(success);
        });

        (await fixture.Application().ExecuteAsync(["login", "--issuer", issuer, "--client-id", "public", "--print"], CancellationToken.None)).ShouldBe(0);

        fixture.Output.ToString().ShouldBe("synthetic-access-token" + Environment.NewLine);
        Directory.Exists(fixture.Home).ShouldBeFalse();
        fixture.Error.ToString().ShouldNotContain("synthetic-access-token", Case.Sensitive);
    }

    /// <summary>Protocol terminal errors stop polling and never persist a token.</summary>
    [Fact(DisplayName = "Cohesion Test [Tooling.Cli] - Login: Should stop on expiration and denial")]
    public async Task Login_WithTerminalError_ShouldStopPollingAsync()
    {
        foreach (string terminal in new[] { "expired_token", "access_denied" })
        {
            int polls = 0;
            using var fixture = new CliFixture((request, _) =>
            {
                if (request.Method == HttpMethod.Get)
                {
                    return Task.FromResult(CliFixture.Json(discovery));
                }
                if (request.RequestUri!.AbsolutePath == "/oauth2/device_authorization")
                {
                    return Task.FromResult(CliFixture.Json(authorization));
                }
                polls++;
                return Task.FromResult(CliFixture.Json("{\"error\":\"" + terminal + "\"}", HttpStatusCode.BadRequest));
            });

            (await fixture.Application().ExecuteAsync(["login", "--issuer", issuer, "--client-id", "public"], CancellationToken.None)).ShouldBe(2);

            polls.ShouldBe(1);
            fixture.Error.ToString().ShouldContain(terminal, Case.Sensitive);
            Directory.Exists(fixture.Home).ShouldBeFalse();
        }
    }

    /// <summary>Local expiration bounds even a server that only replies pending.</summary>
    [Fact(DisplayName = "Cohesion Test [Tooling.Cli] - Login: Should stop polling at device expires_in")]
    public async Task Login_WhenPendingUntilDeadline_ShouldExpireAsync()
    {
        int polls = 0;
        using var fixture = new CliFixture((request, _) =>
        {
            if (request.Method == HttpMethod.Get)
            {
                return Task.FromResult(CliFixture.Json(discovery));
            }
            if (request.RequestUri!.AbsolutePath == "/oauth2/device_authorization")
            {
                return Task.FromResult(CliFixture.Json(authorization.Replace("\"expires_in\":600", "\"expires_in\":3", StringComparison.Ordinal)));
            }
            polls++;
            return Task.FromResult(CliFixture.Json("{\"error\":\"authorization_pending\"}", HttpStatusCode.BadRequest));
        });

        (await fixture.Application().ExecuteAsync(["login", "--issuer", issuer, "--client-id", "public"], CancellationToken.None)).ShouldBe(2);

        polls.ShouldBe(2);
        fixture.Delays.Count.ShouldBe(3);
        fixture.Error.ToString().ShouldContain("expired_token", Case.Sensitive);
    }

    /// <summary>Cancellation is passed to HTTP and prevents credential writes.</summary>
    [Fact(DisplayName = "Cohesion Test [Tooling.Cli] - Login: Should propagate cancellation")]
    public async Task Login_WhenCancelled_ShouldReturn130Async()
    {
        using var cancellation = new CancellationTokenSource();
        using var fixture = new CliFixture((_, token) =>
        {
            token.CanBeCanceled.ShouldBeTrue();
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.FromResult(CliFixture.Json(discovery));
        });

        (await fixture.Application().ExecuteAsync(["login", "--issuer", issuer, "--client-id", "public"], cancellation.Token)).ShouldBe(130);

        Directory.Exists(fixture.Home).ShouldBeFalse();
    }

    /// <summary>Credentials never travel to non-loopback plaintext discovery endpoints.</summary>
    [Fact(DisplayName = "Cohesion Test [Tooling.Cli] - Login: Should reject insecure credential endpoints")]
    public async Task Login_WithRemoteHttp_ShouldRejectBeforeNetworkAsync()
    {
        using var fixture = new CliFixture();

        (await fixture.Application().ExecuteAsync(["login", "--issuer", "http://remote.example", "--client-id", "public"], CancellationToken.None)).ShouldBe(2);

        fixture.Error.ToString().ShouldContain("require HTTPS", Case.Sensitive);
    }

    /// <summary>A response body cannot leak credentials through error reporting.</summary>
    [Fact(DisplayName = "Cohesion Test [Tooling.Cli] - Login: Should report HTTP failure without response bodies")]
    public async Task Login_WithHttpError_ShouldRedactResponseAsync()
    {
        using var fixture = new CliFixture((_, _) => Task.FromResult(CliFixture.Json("{\"error\":\"synthetic-secret\"}", HttpStatusCode.InternalServerError)));

        (await fixture.Application().ExecuteAsync(["login", "--issuer", issuer, "--client-id", "public"], CancellationToken.None)).ShouldBe(2);

        fixture.Error.ToString().ShouldContain("HTTP 500", Case.Sensitive);
        fixture.Error.ToString().ShouldNotContain("synthetic-secret", Case.Sensitive);
    }
}
