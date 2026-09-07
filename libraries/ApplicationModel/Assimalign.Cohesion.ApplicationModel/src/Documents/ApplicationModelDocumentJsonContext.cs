using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Provides NativeAOT-safe JSON metadata for application-model and export documents.
/// </summary>
[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Default,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    WriteIndented = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(ApplicationModelDocument))]
[JsonSerializable(typeof(ApplicationExportDocument))]
internal sealed partial class ApplicationModelDocumentJsonContext : JsonSerializerContext;
