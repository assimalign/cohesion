using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.ApplicationModel.Internal;

/// <summary>
/// Provides NativeAOT-safe JSON metadata for application-model and export documents.
/// </summary>
[global::System.Diagnostics.DebuggerDisplay("ApplicationModelDocumentJsonContext")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Default,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    WriteIndented = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(ApplicationModelDocument))]
[JsonSerializable(typeof(ApplicationModelDocument[]))]
[JsonSerializable(typeof(ApplicationExportDocument))]
internal sealed partial class ApplicationModelDocumentJsonContext : JsonSerializerContext
{
    private sealed class DebugView
    {
        private readonly ApplicationModelDocumentJsonContext _target;

        public DebugView(ApplicationModelDocumentJsonContext value)
        {
            _target = value;
        }

        public global::System.Text.Json.JsonSerializerOptions? GeneratedSerializerOptions => _target.GeneratedSerializerOptions;
        public global::System.Text.Json.JsonSerializerOptions Options => _target.Options;
    }
}
