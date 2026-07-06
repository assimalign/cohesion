using System;
using System.Text.Json;

using Assimalign.Cohesion.OpenApi.Serialization;

namespace Assimalign.Cohesion.OpenApi.Validation;

/// <summary>
/// The official-schema conformance stage: serializes the document for its declared version and
/// evaluates the result against the vendored official OpenAPI meta-schema for that line. Findings are
/// reported as warnings because the specification text — not the schema files — is authoritative per
/// the OpenAPI Initiative publications.
/// </summary>
internal sealed class OpenApiSchemaConformanceRule : IOpenApiValidationRule
{
    private static readonly Lazy<JsonSchemaEvaluator> Schema30 = new(() => Load("oas-3.0-schema.json"));
    private static readonly Lazy<JsonSchemaEvaluator> Schema31 = new(() => Load("oas-3.1-schema.json"));
    private static readonly Lazy<JsonSchemaEvaluator> Schema32 = new(() => Load("oas-3.2-schema.json"));

    public void Validate(OpenApiValidationContext context)
    {
        var version = context.Document.SpecVersion;
        var evaluator = version switch
        {
            OpenApiSpecVersion.V3_0 => Schema30.Value,
            OpenApiSpecVersion.V3_1 => Schema31.Value,
            _ => Schema32.Value
        };

        using var instance = JsonDocument.Parse(OpenApiJson.Serialize(context.Document, version, indented: false));

        foreach (var violation in evaluator.Validate(instance.RootElement))
        {
            context.Warning(
                OpenApiValidationRuleCodes.SchemaViolation,
                $"The serialized document diverges from the official OpenAPI {OpenApiVersionCapabilities.GetVersionString(version)} schema: {violation.Message}.",
                violation.Pointer);
        }
    }

    private static JsonSchemaEvaluator Load(string name)
    {
        using var stream = typeof(OpenApiSchemaConformanceRule).Assembly.GetManifestResourceStream(name)
            ?? throw new OpenApiException($"Embedded schema resource '{name}' was not found.");

        // The schema document must outlive the evaluator, which holds JsonElement views into it.
        return new JsonSchemaEvaluator(JsonDocument.Parse(stream));
    }
}
