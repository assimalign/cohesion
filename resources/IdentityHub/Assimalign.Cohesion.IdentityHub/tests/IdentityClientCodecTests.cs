using System;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.IdentityHub.Internal;

namespace Assimalign.Cohesion.IdentityHub.Tests;

/// <summary>
/// How an app registration is stored: one record in <c>clients</c>, found by its identifier and
/// by each audience, with a payload whose layout is pinned here.
/// </summary>
public class IdentityClientCodecTests
{
    private const string minimal = """{"v":1,"displayName":"orders","accessTokenLifetime":"01:00:00"}""";

    private static readonly DateTimeOffset _now = TestClients.Now;

    private readonly IdentityClientCodec _codec = IdentityClientCodec.Instance;

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Encode: Should key the record by the identifier and index each audience uniquely")]
    public void Encode_WithARegistration_ShouldKeyByIdentifierAndIndexEachAudience()
    {
        // Arrange
        IdentityClient client = IdentityClient.Create(new()
        {
            Id = "orders-api",
            Audiences = { "api://orders", "https://orders.example/api" },
        });

        // Act
        IdentityHubRecord record = _codec.Encode(client);

        // Assert
        _codec.Set.Value.ShouldBe("clients");
        record.Key.Value.ShouldBe("orders-api");
        record.ExpiresAt.ShouldBeNull();
        record.Indexes.Count.ShouldBe(2);
        record.Indexes.ShouldAllBe(index => index.Name.Value == "audience" && index.IsUnique);
        record.Indexes.Select(index => index.Value.Value).ShouldBe(["api://orders", "https://orders.example/api"]);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Encode: Should write the pinned layout")]
    public void Encode_WithACompleteRegistration_ShouldWriteThePinnedLayout()
    {
        // Arrange
        IdentityClient client = Complete(out _);

        // Act
        IdentityHubRecord record = _codec.Encode(client);
        using JsonDocument document = JsonDocument.Parse(record.Payload);
        JsonElement root = document.RootElement;

        // Assert
        root.EnumerateObject().Select(property => property.Name).ShouldBe(
        [
            "v", "displayName", "description", "grants", "accessTokenLifetime", "redirects",
            "credentials", "audiences", "scopes", "roles", "permissions",
        ]);
        root.GetProperty("v").GetInt32().ShouldBe(1);
        root.GetProperty("displayName").GetString().ShouldBe("Orders");
        root.GetProperty("accessTokenLifetime").GetString().ShouldBe("00:20:00");
        root.GetProperty("grants").EnumerateArray().Select(grant => grant.GetString()).ShouldBe(
        [
            "authorization_code",
            "client_credentials",
            "urn:ietf:params:oauth:grant-type:device_code",
            "refresh_token",
        ]);
        root.GetProperty("redirects").EnumerateArray().Select(redirect => redirect.GetProperty("platform").GetString())
            .ShouldBe(["web", "spa", "native"]);
        root.GetProperty("redirects")[0].EnumerateObject().Select(property => property.Name).ShouldBe(["uri", "platform"]);

        JsonElement credentials = root.GetProperty("credentials");
        credentials.EnumerateArray().Select(credential => credential.GetProperty("kind").GetString())
            .ShouldBe(["secret", "certificate", "federated"]);
        credentials[0].EnumerateObject().Select(property => property.Name)
            .ShouldBe(["id", "kind", "displayName", "notBefore", "expiresAt", "salt", "verifier", "iterations"]);
        credentials[1].EnumerateObject().Select(property => property.Name)
            .ShouldBe(["id", "kind", "notBefore", "expiresAt", "certificate"]);
        credentials[2].EnumerateObject().Select(property => property.Name)
            .ShouldBe(["id", "kind", "issuer", "subject", "audiences"]);

        root.GetProperty("audiences").EnumerateArray().Select(audience => audience.GetString()).ShouldBe(["api://orders"]);
        root.GetProperty("scopes")[0].EnumerateObject().Select(property => property.Name)
            .ShouldBe(["name", "displayName", "description"]);
        root.GetProperty("roles")[0].GetProperty("members").EnumerateArray().Select(member => member.GetString())
            .ShouldBe(["user", "application"]);
        root.GetProperty("permissions").EnumerateArray().Select(permission => permission.GetProperty("kind").GetString())
            .ShouldBe(["delegated", "application"]);
        root.GetProperty("permissions")[0].EnumerateObject().Select(property => property.Name)
            .ShouldBe(["resource", "name", "kind"]);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Encode: Should leave out what a registration does not have")]
    public void Encode_WithARegistrationThatOnlyNamesItself_ShouldWriteNoEmptyMember()
    {
        // Arrange
        IdentityClient client = IdentityClient.Create(new() { Id = "orders" });

        // Act
        string payload = Encoding.UTF8.GetString(_codec.Encode(client).Payload.Span);

        // Assert
        payload.ShouldBe(minimal);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Encode: Should never write a secret, only its verifier")]
    public void Encode_WithASecretCredential_ShouldNotWriteTheSecret()
    {
        // Arrange
        const string chosen = "correct-horse-battery-staple";
        IdentityClientSecretCredential generated = IdentityClientSecretCredential.Generate("generated", out string secret);
        IdentityClient client = IdentityClient.Create(new()
        {
            Id = "orders-daemon",
            Credentials = { generated, IdentityClientSecretCredential.Create("chosen", chosen) },
        });

        // Act
        IdentityHubRecord record = _codec.Encode(client);
        string payload = Encoding.UTF8.GetString(record.Payload.Span);

        // Assert
        payload.ShouldNotContain(secret, Case.Sensitive);
        payload.ShouldNotContain(chosen, Case.Sensitive);
        payload.ShouldContain("\"verifier\"", Case.Sensitive);
        record.Key.Value.ShouldNotContain(secret, Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Decode: Should read back every part of a registration")]
    public void Decode_WithAnEncodedRegistration_ShouldReadBackEveryPart()
    {
        // Arrange
        IdentityClient client = Complete(out string secret);

        // Act
        IdentityClient decoded = _codec.Decode(_codec.Encode(client));

        // Assert
        decoded.Id.ShouldBe(client.Id);
        decoded.DisplayName.ShouldBe(client.DisplayName);
        decoded.Description.ShouldBe(client.Description);
        decoded.Grants.ShouldBe(client.Grants);
        decoded.AccessTokenLifetime.ShouldBe(client.AccessTokenLifetime);
        decoded.Redirects.ShouldBe(client.Redirects);
        decoded.Audiences.ShouldBe(client.Audiences);
        decoded.Permissions.ShouldBe(client.Permissions);
        decoded.IsLocked.ShouldBeFalse();
        decoded.Version.ShouldBeNull();

        decoded.Scopes.Count.ShouldBe(1);
        decoded.Scopes[0].Name.ShouldBe(client.Scopes[0].Name);
        decoded.Scopes[0].DisplayName.ShouldBe("Read orders");
        decoded.Scopes[0].Description.ShouldBe("Read the orders of the signed-in user.");
        decoded.Roles.Count.ShouldBe(1);
        decoded.Roles[0].Name.ShouldBe(client.Roles[0].Name);
        decoded.Roles[0].Members.ShouldBe(IdentityClientRoleMembers.Users | IdentityClientRoleMembers.Applications);
        decoded.Roles[0].DisplayName.ShouldBe("orders.process");
        decoded.Roles[0].Description.ShouldBeNull();

        decoded.Credentials.Count.ShouldBe(3);
        IdentityClientSecretCredential decodedSecret = decoded.Credentials[0].ShouldBeOfType<IdentityClientSecretCredential>();
        decodedSecret.Id.Value.ShouldBe("primary");
        decodedSecret.DisplayName.ShouldBe("Primary");
        decodedSecret.NotBefore.ShouldBe(_now);
        decodedSecret.ExpiresAt.ShouldBe(_now.AddDays(90));
        decodedSecret.Verify(secret, _now).ShouldBeTrue();
        decodedSecret.Verify(secret + "x", _now).ShouldBeFalse();

        IdentityClientCertificateCredential original = (IdentityClientCertificateCredential)client.Credentials[1];
        IdentityClientCertificateCredential decodedCertificate = decoded.Credentials[1].ShouldBeOfType<IdentityClientCertificateCredential>();
        decodedCertificate.Certificate.ToArray().ShouldBe(original.Certificate.ToArray());
        decodedCertificate.Thumbprint.ShouldBe(original.Thumbprint);
        decodedCertificate.NotBefore.ShouldBe(original.NotBefore);
        decodedCertificate.ExpiresAt.ShouldBe(original.ExpiresAt);

        IdentityClientFederatedCredential decodedFederated = decoded.Credentials[2].ShouldBeOfType<IdentityClientFederatedCredential>();
        decodedFederated.Issuer.ShouldBe("https://token.actions.example");
        decodedFederated.Subject.ShouldBe("repo:assimalign/orders:ref:refs/heads/main");
        decodedFederated.Audiences.Single().Value.ShouldBe("api://token-exchange");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Decode: Should read the smallest payload as a registration with the defaults")]
    public void Decode_WithTheSmallestPayload_ShouldApplyTheDefaults()
    {
        // Act
        IdentityClient client = _codec.Decode(Record(minimal));

        // Assert
        client.Id.Value.ShouldBe("orders");
        client.DisplayName.ShouldBe("orders");
        client.Grants.ShouldBe(IdentityClientGrants.None);
        client.AccessTokenLifetime.ShouldBe(TimeSpan.FromHours(1));
        client.Audiences.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Decode: Should keep text that is not ASCII")]
    public void Decode_WithTextThatIsNotAscii_ShouldRoundTripIt()
    {
        // Arrange
        IdentityClient client = IdentityClient.Create(new()
        {
            Id = "orders",
            DisplayName = "Commandes — 注文 \U0001F4E6",
            Description = "L'API des commandes <b>&</b> \"livraisons\".\n\tDeux lignes.",
        });

        // Act
        IdentityClient decoded = _codec.Decode(_codec.Encode(client));

        // Assert
        decoded.DisplayName.ShouldBe(client.DisplayName);
        decoded.Description.ShouldBe(client.Description);
    }

    [Theory(DisplayName = "Cohesion Test [IdentityHub] - Decode: Should refuse a member the layout does not know")]
    [InlineData("""{"v":1,"displayName":"orders","accessTokenLifetime":"01:00:00","tenant":"a"}""")]
    [InlineData("""{"v":1,"displayName":"orders","accessTokenLifetime":"01:00:00","grants":["authorization_code"],"redirects":[{"uri":"https://orders.example/signin","platform":"web","wildcard":true}]}""")]
    [InlineData("""{"v":1,"displayName":"orders","accessTokenLifetime":"01:00:00","permissions":[{"resource":"billing-api","name":"invoices.read","kind":"delegated","consent":"admin"}]}""")]
    public void Decode_WithAnUnknownMember_ShouldThrowJsonException(string payload)
    {
        // Act + Assert
        Should.Throw<JsonException>(() => _codec.Decode(Record(payload)));
    }

    [Theory(DisplayName = "Cohesion Test [IdentityHub] - Decode: Should refuse a layout version it does not read")]
    [InlineData("""{"v":2,"displayName":"orders","accessTokenLifetime":"01:00:00"}""", "2")]
    [InlineData("""{"displayName":"orders","accessTokenLifetime":"01:00:00"}""", "0")]
    public void Decode_WithAnotherLayoutVersion_ShouldThrowFormatException(string payload, string version)
    {
        // Act
        FormatException exception = Should.Throw<FormatException>(() => _codec.Decode(Record(payload)));

        // Assert
        exception.Message.ShouldContain(version);
    }

    [Theory(DisplayName = "Cohesion Test [IdentityHub] - Decode: Should refuse a payload that does not follow the layout")]
    [InlineData("null")]
    [InlineData("""{"v":1,"accessTokenLifetime":"01:00:00"}""")]
    [InlineData("""{"v":1,"displayName":"orders","accessTokenLifetime":"01:00:00","grants":["implicit"]}""")]
    [InlineData("""{"v":1,"displayName":"orders","accessTokenLifetime":"01:00:00","grants":[null]}""")]
    [InlineData("""{"v":1,"displayName":"orders","accessTokenLifetime":"01:00:00","grants":["authorization_code"],"redirects":[{"uri":"https://orders.example/signin","platform":"desktop"}]}""")]
    [InlineData("""{"v":1,"displayName":"orders","accessTokenLifetime":"01:00:00","grants":["authorization_code"],"redirects":[null]}""")]
    [InlineData("""{"v":1,"displayName":"orders","accessTokenLifetime":"01:00:00","audiences":[null]}""")]
    [InlineData("""{"v":1,"displayName":"orders","accessTokenLifetime":"01:00:00","credentials":[{"id":"primary","kind":"password"}]}""")]
    [InlineData("""{"v":1,"displayName":"orders","accessTokenLifetime":"01:00:00","credentials":[{"id":"primary"}]}""")]
    [InlineData("""{"v":1,"displayName":"orders","accessTokenLifetime":"01:00:00","credentials":[{"kind":"secret"}]}""")]
    [InlineData("""{"v":1,"displayName":"orders","accessTokenLifetime":"01:00:00","credentials":[{"id":"primary","kind":"secret","salt":"AAAAAAAAAAAAAAAAAAAAAA==","iterations":1}]}""")]
    [InlineData("""{"v":1,"displayName":"orders","accessTokenLifetime":"01:00:00","credentials":[{"id":"primary","kind":"secret","salt":"AAAAAAAAAAAAAAAAAAAAAA==","verifier":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=","iterations":1,"issuer":"https://token.actions.example"}]}""")]
    [InlineData("""{"v":1,"displayName":"orders","accessTokenLifetime":"01:00:00","credentials":[{"id":"signing","kind":"certificate","certificate":"AAAA"}]}""")]
    [InlineData("""{"v":1,"displayName":"orders","accessTokenLifetime":"01:00:00","credentials":[{"id":"build","kind":"federated","issuer":"https://token.actions.example","subject":"repo"}]}""")]
    [InlineData("""{"v":1,"displayName":"orders","accessTokenLifetime":"01:00:00","credentials":[{"id":"build","kind":"federated","issuer":"https://token.actions.example","subject":"repo","audiences":["orders"],"iterations":1}]}""")]
    [InlineData("""{"v":1,"displayName":"orders","accessTokenLifetime":"01:00:00","audiences":["api://orders"],"scopes":[{"name":"orders.read"}]}""")]
    [InlineData("""{"v":1,"displayName":"orders","accessTokenLifetime":"01:00:00","audiences":["api://orders"],"roles":[{"name":"orders.process","displayName":"Process"}]}""")]
    [InlineData("""{"v":1,"displayName":"orders","accessTokenLifetime":"01:00:00","audiences":["api://orders"],"roles":[{"name":"orders.process","displayName":"Process","members":["group"]}]}""")]
    [InlineData("""{"v":1,"displayName":"orders","accessTokenLifetime":"01:00:00","permissions":[{"resource":"billing-api","name":"invoices.read","kind":"admin"}]}""")]
    [InlineData("""{"v":1,"displayName":"orders","accessTokenLifetime":"01:00:00","permissions":[{"name":"invoices.read","kind":"delegated"}]}""")]
    public void Decode_WithAPayloadOutsideTheLayout_ShouldThrowFormatException(string payload)
    {
        // Act + Assert
        Should.Throw<FormatException>(() => _codec.Decode(Record(payload)));
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Decode: Should refuse a payload that is not JSON")]
    public void Decode_WithBytesThatAreNotJson_ShouldThrowJsonException()
    {
        // Act + Assert
        Should.Throw<JsonException>(() => _codec.Decode(Record("not json")));
        Should.Throw<JsonException>(() => _codec.Decode(Record(string.Empty)));
    }

    [Theory(DisplayName = "Cohesion Test [IdentityHub] - Decode: Should check a stored registration as it checks a new one")]
    [InlineData("""{"v":1,"displayName":"orders","accessTokenLifetime":"01:00:00","grants":["authorization_code"]}""")]
    [InlineData("""{"v":1,"displayName":"orders","accessTokenLifetime":"00:00:00"}""")]
    [InlineData("""{"v":1,"displayName":"orders","accessTokenLifetime":"01:00:00","audiences":["api://orders","api://orders"]}""")]
    [InlineData("""{"v":1,"displayName":"orders","accessTokenLifetime":"01:00:00","audiences":["a","b","c","d","e","f","g","h","i"]}""")]
    [InlineData("""{"v":1,"displayName":"orders","accessTokenLifetime":"01:00:00","audiences":["not an audience"]}""")]
    [InlineData("""{"v":1,"displayName":"orders","accessTokenLifetime":"01:00:00","grants":["authorization_code"],"redirects":[{"uri":"http://orders.example/signin","platform":"web"}]}""")]
    [InlineData("""{"v":1,"displayName":"orders","accessTokenLifetime":"01:00:00","scopes":[{"name":"orders.read","displayName":"Read"}]}""")]
    public void Decode_WithAPayloadThatBreaksARegistrationRule_ShouldThrowArgumentException(string payload)
    {
        // Act + Assert
        Should.Throw<ArgumentException>(() => _codec.Decode(Record(payload)));
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Decode: Should refuse a record whose key is not a client identifier")]
    public void Decode_WithAKeyThatIsNoClientIdentifier_ShouldThrowArgumentException()
    {
        // Act + Assert
        Should.Throw<ArgumentException>(() => _codec.Decode(Record(minimal, "not a client id")));
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Encode, Decode: Should refuse null")]
    public void EncodeAndDecode_WithNull_ShouldThrowArgumentNullException()
    {
        // Act + Assert
        Should.Throw<ArgumentNullException>(() => _codec.Encode(null!));
        Should.Throw<ArgumentNullException>(() => _codec.Decode(null!));
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Encode: Should fit a registration at every limit inside one record")]
    public void Encode_WithARegistrationAtEveryLimit_ShouldFitInsideOneRecord()
    {
        // Arrange: every list at its limit, every string at its longest, the largest credential
        // kind, and text that takes three bytes a character.
        string wideName = new('鍵', IdentityClient.MaxDisplayNameLength);
        string wideDescription = new('鍵', IdentityClient.MaxDescriptionLength);
        var descriptor = new IdentityClientDescriptor
        {
            Id = new string('c', IdentityClientId.MaxLength),
            DisplayName = wideName,
            Description = wideDescription,
            Grants = IdentityClientGrants.AuthorizationCode | IdentityClientGrants.ClientCredentials |
                IdentityClientGrants.DeviceCode | IdentityClientGrants.RefreshToken,
            AccessTokenLifetime = IdentityClient.MaxAccessTokenLifetime,
        };

        for (int index = 0; index < IdentityClient.MaxRedirectCount; index++)
        {
            descriptor.Redirects.Add(IdentityClientRedirect.Web(Longest($"https://orders.example/{index:D3}/", IdentityClientRedirect.MaxLength)));
        }

        for (int index = 0; index < IdentityClient.MaxCredentialCount; index++)
        {
            descriptor.Credentials.Add(IdentityClientCertificateCredential.Create(
                Longest($"{index:D3}", IdentityClientCredentialId.MaxLength),
                LargeCertificate(),
                wideName));
        }

        for (int index = 0; index < IdentityClient.MaxAudienceCount; index++)
        {
            descriptor.Audiences.Add(Longest($"api://{index:D3}/", IdentityClientAudience.MaxLength));
        }

        for (int index = 0; index < IdentityClient.MaxScopeCount; index++)
        {
            descriptor.Scopes.Add(IdentityClientScope.Create(
                Longest($"s{index:D3}", IdentityClientPermissionName.MaxLength),
                wideName,
                wideDescription));
        }

        for (int index = 0; index < IdentityClient.MaxRoleCount; index++)
        {
            descriptor.Roles.Add(IdentityClientRole.Create(
                Longest($"r{index:D3}", IdentityClientPermissionName.MaxLength),
                IdentityClientRoleMembers.Users | IdentityClientRoleMembers.Applications,
                wideName,
                wideDescription));
        }

        for (int index = 0; index < IdentityClient.MaxPermissionCount; index++)
        {
            descriptor.Permissions.Add(IdentityClientPermission.Application(
                new string('b', IdentityClientId.MaxLength),
                Longest($"p{index:D3}", IdentityClientPermissionName.MaxLength)));
        }

        IdentityClient client = IdentityClient.Create(descriptor);

        // Act
        IdentityHubRecord record = _codec.Encode(client);
        IdentityClient decoded = _codec.Decode(record);

        // Assert
        record.Payload.Length.ShouldBeLessThan(IdentityHubRecord.MaxPayloadLength / 2);
        record.Indexes.Count.ShouldBeLessThanOrEqualTo(IdentityHubRecord.MaxIndexCount);
        decoded.Redirects.Count.ShouldBe(IdentityClient.MaxRedirectCount);
        decoded.Credentials.Count.ShouldBe(IdentityClient.MaxCredentialCount);
        decoded.Scopes.Count.ShouldBe(IdentityClient.MaxScopeCount);
        decoded.Roles.Count.ShouldBe(IdentityClient.MaxRoleCount);
        decoded.Permissions.Count.ShouldBe(IdentityClient.MaxPermissionCount);
        decoded.DisplayName.ShouldBe(wideName);
    }

    private static IdentityHubRecord Record(string payload, string key = "orders")
    {
        return new IdentityHubRecord(new IdentityHubRecordKey(key), Encoding.UTF8.GetBytes(payload));
    }

    private static string Longest(string prefix, int length)
    {
        return prefix + new string('x', length - prefix.Length);
    }

    private static byte[] LargeCertificate()
    {
        // An extension pads the certificate to just under the largest one a credential keeps.
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=orders-daemon", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509Extension("1.3.6.1.4.1.55555.1", new byte[7600], critical: false));
        using X509Certificate2 certificate = request.CreateSelfSigned(_now, _now.AddDays(30));
        certificate.RawData.Length.ShouldBeInRange(7900, IdentityClientCertificateCredential.MaxCertificateLength);
        return certificate.RawData;
    }

    private static IdentityClient Complete(out string secret)
    {
        using X509Certificate2 certificate = TestClients.Certificate(_now, _now.AddDays(365));
        return IdentityClient.Create(new()
        {
            Id = "orders",
            DisplayName = "Orders",
            Description = "Takes orders.",
            Grants = IdentityClientGrants.AuthorizationCode | IdentityClientGrants.ClientCredentials |
                IdentityClientGrants.DeviceCode | IdentityClientGrants.RefreshToken,
            AccessTokenLifetime = TimeSpan.FromMinutes(20),
            Redirects =
            {
                IdentityClientRedirect.Web("https://orders.example/signin"),
                IdentityClientRedirect.SinglePage("http://localhost:5173/callback"),
                IdentityClientRedirect.Native("com.example.orders:/callback"),
            },
            Credentials =
            {
                IdentityClientSecretCredential.Generate("primary", out secret, "Primary", _now, _now.AddDays(90)),
                IdentityClientCertificateCredential.Create("signing", certificate.RawData),
                IdentityClientFederatedCredential.Create(
                    "build",
                    "https://token.actions.example",
                    "repo:assimalign/orders:ref:refs/heads/main",
                    ["api://token-exchange"]),
            },
            Audiences = { "api://orders" },
            Scopes = { IdentityClientScope.Create("orders.read", "Read orders", "Read the orders of the signed-in user.") },
            Roles =
            {
                IdentityClientRole.Create("orders.process", IdentityClientRoleMembers.Users | IdentityClientRoleMembers.Applications),
            },
            Permissions =
            {
                IdentityClientPermission.Delegated("billing-api", "invoices.read"),
                IdentityClientPermission.Application("orders", "orders.process"),
            },
        });
    }
}
