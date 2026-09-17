using System;
using System.Buffers;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Assimalign.Cohesion.IdentityModel;
using Assimalign.Cohesion.IdentityModel.Token.JsonWebToken;

namespace Assimalign.Cohesion.ConfigurationStore.Hosting.Tests;

internal sealed class TestBootstrapIdentity : IDisposable
{
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly string _keyId;

    internal TestBootstrapIdentity()
    {
        PublicKey = CreatePublicKey(_key, out _keyId);
    }

    internal ReadOnlyMemory<byte> PublicKey { get; }

    internal string Issue(string audience)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var descriptor = new JsonWebTokenDescriptor
        {
            Id = Guid.NewGuid().ToString("N"),
            Issuer = "appa",
            Subject = new SubjectIdentifier("local", issuer: "appa"),
            TokenType = "JWT",
            IssuedAt = now,
            NotBefore = now,
            ExpiresAt = now.AddHours(1),
        };
        descriptor.Audiences.Add(audience);
        return JsonWebTokenWriter.CreateEs256(_key, _keyId).Write(descriptor);
    }

    public void Dispose()
    {
        _key.Dispose();
    }

    private static byte[] CreatePublicKey(ECDsa key, out string keyId)
    {
        ECParameters parameters = key.ExportParameters(includePrivateParameters: false);
        string x = Base64Url.EncodeToString(parameters.Q.X!);
        string y = Base64Url.EncodeToString(parameters.Q.Y!);

        var canonical = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(canonical))
        {
            writer.WriteStartObject();
            writer.WriteString("crv", "P-256");
            writer.WriteString("kty", "EC");
            writer.WriteString("x", x);
            writer.WriteString("y", y);
            writer.WriteEndObject();
        }

        keyId = Base64Url.EncodeToString(SHA256.HashData(canonical.WrittenSpan));
        var publicKey = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(publicKey))
        {
            writer.WriteStartObject();
            writer.WriteString("kty", "EC");
            writer.WriteString("crv", "P-256");
            writer.WriteString("x", x);
            writer.WriteString("y", y);
            writer.WriteString("kid", keyId);
            writer.WriteString("alg", "ES256");
            writer.WriteString("use", "sig");
            writer.WriteEndObject();
        }

        return publicKey.WrittenSpan.ToArray();
    }
}
