using System;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace Assimalign.Cohesion.Web.Authorization.Tests.TestObjects;

/// <summary>
/// Mints compact-serialized HS256 tokens for the JWT Bearer end-to-end tests. The header and payload
/// JSON are hand-authored, as in the Web.Authentication.Bearer tests, so a test controls the claims the
/// handler maps onto the principal.
/// </summary>
internal static class TestJwt
{
    public const string Issuer = "https://issuer.example";
    public const string Audience = "api://authorization-tests";

    public static string Create(byte[] key, string subject, params string[] roles)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        StringBuilder payload = new();
        payload.Append('{');
        payload.Append($"\"sub\":\"{subject}\",\"name\":\"{subject}\"");
        payload.Append($",\"iss\":\"{Issuer}\",\"aud\":\"{Audience}\"");

        if (roles.Length > 0)
        {
            payload.Append(",\"roles\":[");
            for (int i = 0; i < roles.Length; i++)
            {
                if (i > 0)
                {
                    payload.Append(',');
                }

                payload.Append($"\"{roles[i]}\"");
            }

            payload.Append(']');
        }

        payload.Append($",\"iat\":{now.ToUnixTimeSeconds()},\"exp\":{now.AddHours(1).ToUnixTimeSeconds()}");
        payload.Append('}');

        string signingInput = Segment("{\"alg\":\"HS256\",\"typ\":\"JWT\"}") + "." + Segment(payload.ToString());
        byte[] signature = HMACSHA256.HashData(key, Encoding.ASCII.GetBytes(signingInput));

        return signingInput + "." + Base64Url.EncodeToString(signature);
    }

    private static string Segment(string json) => Base64Url.EncodeToString(Encoding.UTF8.GetBytes(json));
}
