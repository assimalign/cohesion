using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.Web.Hosting.Management.Internal;

[System.Diagnostics.DebuggerDisplay("CertificateProviderJsonConverter")]
[System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed class CertificateProviderJsonConverter : JsonConverter<ResourceCertificateProvider>
{
    public override ResourceCertificateProvider Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String || !ResourceCertificateProvider.TryCreate(reader.GetString()!, out ResourceCertificateProvider value))
        {
            throw new JsonException(ErrorMessages.CertificateReferenceInvalid);
        }
        return value;
    }

    public override void Write(Utf8JsonWriter writer, ResourceCertificateProvider value, JsonSerializerOptions options)
    {
        if (value.Value is null) { throw new JsonException(ErrorMessages.CertificateReferenceInvalid); }
        writer.WriteStringValue(value.Value);
    }

    private sealed class DebugView
    {
        private readonly CertificateProviderJsonConverter _target;
        public DebugView(CertificateProviderJsonConverter value) => _target = value;
        public Type? Type => _target.Type;
        public bool HandleNull => _target.HandleNull;
    }
}
