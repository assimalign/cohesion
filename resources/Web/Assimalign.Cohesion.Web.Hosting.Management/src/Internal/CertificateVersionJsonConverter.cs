using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.Web.Hosting.Management.Internal;

[System.Diagnostics.DebuggerDisplay("CertificateVersionJsonConverter")]
[System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed class CertificateVersionJsonConverter : JsonConverter<ResourceCertificateVersion>
{
    public override ResourceCertificateVersion Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String || !ResourceCertificateVersion.TryCreate(reader.GetString()!, out ResourceCertificateVersion value))
        {
            throw new JsonException(ErrorMessages.CertificateReferenceInvalid);
        }
        return value;
    }

    public override void Write(Utf8JsonWriter writer, ResourceCertificateVersion value, JsonSerializerOptions options)
    {
        if (value.Value is null) { throw new JsonException(ErrorMessages.CertificateReferenceInvalid); }
        writer.WriteStringValue(value.Value);
    }

    private sealed class DebugView
    {
        private readonly CertificateVersionJsonConverter _target;
        public DebugView(CertificateVersionJsonConverter value) => _target = value;
        public Type? Type => _target.Type;
        public bool HandleNull => _target.HandleNull;
    }
}
