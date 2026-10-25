using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.Web.Hosting.Management.Internal;

[System.Diagnostics.DebuggerDisplay("CertificateKeyJsonConverter")]
[System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed class CertificateKeyJsonConverter : JsonConverter<ResourceCertificateKey>
{
    public override ResourceCertificateKey Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String || !ResourceCertificateKey.TryCreate(reader.GetString()!, out ResourceCertificateKey value))
        {
            throw new JsonException(ErrorMessages.CertificateReferenceInvalid);
        }
        return value;
    }

    public override void Write(Utf8JsonWriter writer, ResourceCertificateKey value, JsonSerializerOptions options)
    {
        if (value.Value is null) { throw new JsonException(ErrorMessages.CertificateReferenceInvalid); }
        writer.WriteStringValue(value.Value);
    }

    private sealed class DebugView
    {
        private readonly CertificateKeyJsonConverter _target;
        public DebugView(CertificateKeyJsonConverter value) => _target = value;
        public Type? Type => _target.Type;
        public bool HandleNull => _target.HandleNull;
    }
}
