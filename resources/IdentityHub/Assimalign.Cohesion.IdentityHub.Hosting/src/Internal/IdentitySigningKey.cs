using System;
using System.Buffers;
using System.Buffers.Text;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.IdentityModel;
using Assimalign.Cohesion.IdentityModel.Token.JsonWebToken;

namespace Assimalign.Cohesion.IdentityHub.Hosting;

internal sealed class IdentitySigningKey : IDisposable
{
    private const string KeyFileName = "identity-signing-key.pk8";
    private readonly object _gate = new();
    private readonly ECDsa _key;
    private readonly IJsonWebTokenWriter _writer;
    private readonly string _x;
    private readonly string _y;

    private IdentitySigningKey(ECDsa key)
    {
        _key = key;
        ECParameters parameters = key.ExportParameters(includePrivateParameters: false);
        _x = Base64Url.EncodeToString(parameters.Q.X!);
        _y = Base64Url.EncodeToString(parameters.Q.Y!);
        KeyId = ComputeKeyId(_x, _y);
        _writer = JsonWebTokenWriter.CreateEs256(key, KeyId);
    }

    internal string KeyId { get; }

    internal static async Task<IdentitySigningKey> LoadOrCreateAsync(
        string dataPath,
        CancellationToken cancellationToken)
    {
        string path = Path.Combine(dataPath, KeyFileName);
        if (!File.Exists(path))
        {
            using ECDsa generated = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            byte[] privateKey = generated.ExportPkcs8PrivateKey();
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllBytesAsync(temporary, privateKey, cancellationToken).ConfigureAwait(false);
                Harden(temporary);
                try
                {
                    File.Move(temporary, path);
                }
                catch (IOException) when (File.Exists(path))
                {
                    File.Delete(temporary);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(privateKey);
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
        }

        Harden(path);
        byte[] persisted = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        try
        {
            ECDsa key = ECDsa.Create();
            try
            {
                key.ImportPkcs8PrivateKey(persisted, out int bytesRead);
                if (bytesRead != persisted.Length ||
                    key.ExportParameters(false).Curve.Oid.Value != ECCurve.NamedCurves.nistP256.Oid.Value)
                {
                    throw new CryptographicException("The persisted IdentityHub signing key is not a P-256 key.");
                }

                return new IdentitySigningKey(key);
            }
            catch
            {
                key.Dispose();
                throw;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(persisted);
        }
    }

    internal string Issue(
        string issuer,
        string subject,
        string audience,
        string clientId,
        string? scope,
        TimeSpan lifetime,
        DateTimeOffset now,
        bool identityToken = false)
    {
        var descriptor = new JsonWebTokenDescriptor
        {
            Id = Guid.NewGuid().ToString("N"),
            Issuer = issuer,
            Subject = new SubjectIdentifier(subject, issuer: issuer),
            TokenType = identityToken ? "JWT" : "at+jwt",
            IssuedAt = now,
            NotBefore = now,
            ExpiresAt = now.Add(lifetime),
        };
        descriptor.Audiences.Add(audience);
        descriptor.Claims.Add(new IdentityClaim("client_id", clientId));
        descriptor.Claims.Add(new IdentityClaim("token_use", identityToken ? "id" : "access"));
        if (!string.IsNullOrWhiteSpace(scope))
        {
            descriptor.Claims.Add(new IdentityClaim("scope", scope));
        }

        lock (_gate)
        {
            return _writer.Write(descriptor);
        }
    }

    internal void WriteJwks(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("keys");
        writer.WriteStartArray();
        writer.WriteStartObject();
        writer.WriteString("kty", "EC");
        writer.WriteString("use", "sig");
        writer.WriteString("alg", "ES256");
        writer.WriteString("kid", KeyId);
        writer.WriteString("crv", "P-256");
        writer.WriteString("x", _x);
        writer.WriteString("y", _y);
        writer.WriteEndObject();
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    public void Dispose() => _key.Dispose();

    private static string ComputeKeyId(string x, string y)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("crv", "P-256");
            writer.WriteString("kty", "EC");
            writer.WriteString("x", x);
            writer.WriteString("y", y);
            writer.WriteEndObject();
        }

        byte[] hash = SHA256.HashData(buffer.WrittenSpan);
        try
        {
            return Base64Url.EncodeToString(hash);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(hash);
        }
    }

    private static void Harden(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}
