using System;
using System.Collections.Generic;

using Assimalign.Cohesion.OpenApi.Serialization;
using Assimalign.Cohesion.OpenApi.Validation;

namespace Assimalign.Cohesion.OpenApi.Versioning;

/// <summary>
/// Transforms an <see cref="OpenApiDocument"/> from its declared line to a target line, applying the
/// documented construct changes and reporting a diagnostic for every conversion and every construct that
/// cannot translate. The input document is never mutated: the transform works on a deep copy produced by
/// a serialization round-trip.
/// </summary>
/// <remarks>
/// The model already normalizes the two differences that are otherwise the crux of a 3.0↔3.1 conversion —
/// nullability (<c>Type</c> + <c>Nullable</c>) and exclusive numeric bounds — so the serializer emits the
/// right wire form for the target line without a transform. This transformer handles the remaining
/// construct-level changes (binary formats, <c>example</c>/<c>examples</c>, <c>const</c>, multi-type,
/// XML node types) and uses the shared capability matrix, through the validator, to report every field
/// the target line does not support so the caller sees exactly what an upgrade or downgrade costs.
/// </remarks>
public static class OpenApiVersionTransformer
{
    /// <summary>
    /// Transforms a document to a target OpenAPI line.
    /// </summary>
    /// <param name="document">The source document.</param>
    /// <param name="target">The line to target.</param>
    /// <returns>The transformed document (a copy) and the findings raised.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="document"/> is <see langword="null"/>.</exception>
    public static OpenApiTransformResult Transform(OpenApiDocument document, OpenApiSpecVersion target)
    {
        ArgumentNullException.ThrowIfNull(document);

        var source = document.SpecVersion;
        var copy = DeepCopy(document);
        copy.SpecVersion = target;

        var diagnostics = new List<OpenApiTransformDiagnostic>();
        if (source == target)
        {
            return new OpenApiTransformResult(copy, diagnostics);
        }

        var upgrading = (int)target > (int)source;
        foreach (var (schema, pointer) in OpenApiSchemaWalker.Walk(copy))
        {
            if (upgrading)
            {
                ApplyUpgrade(schema, source, target, pointer, diagnostics);
            }
            else
            {
                ApplyDowngrade(schema, source, target, pointer, diagnostics);
            }
        }

        AnalyzeVersionFit(copy, target, diagnostics);
        return new OpenApiTransformResult(copy, diagnostics);
    }

    private static OpenApiDocument DeepCopy(OpenApiDocument document) =>
        // Serialize at the highest line (the superset) so no construct is gated away by the writer: a
        // faithful copy must survive an upgrade, where the source line is lower than what the document
        // may already carry. The model normalizes the only two version-specific wire forms (nullability
        // and exclusive bounds), so a 3.2 round-trip preserves every model field.
        OpenApiJson.Parse(document.ToJson(OpenApiSpecVersion.V3_2, indented: false));

    private static void ApplyUpgrade(OpenApiSchema schema, OpenApiSpecVersion source, OpenApiSpecVersion target, string pointer, List<OpenApiTransformDiagnostic> diagnostics)
    {
        // Binary and byte formats moved to content keywords when crossing the 3.0 → 3.1 boundary.
        if (source == OpenApiSpecVersion.V3_0)
        {
            if (schema.Format == "byte")
            {
                schema.ContentEncoding ??= "base64";
                schema.Format = null;
                diagnostics.Add(Info(OpenApiTransformDiagnosticCodes.BinaryFormatConverted, "Converted 'format: byte' to 'contentEncoding: base64'.", pointer));
            }
            else if (schema.Format == "binary")
            {
                schema.ContentMediaType ??= "application/octet-stream";
                schema.Format = null;
                diagnostics.Add(Info(OpenApiTransformDiagnosticCodes.BinaryFormatConverted, "Converted 'format: binary' to 'contentMediaType: application/octet-stream'.", pointer));
            }

            if (schema.Example is not null && schema.Examples.Count == 0)
            {
                schema.Examples.Add(schema.Example);
                schema.Example = null;
                diagnostics.Add(Info(OpenApiTransformDiagnosticCodes.SchemaExampleConverted, "Moved the singular 'example' into the 'examples' array (the singular form is deprecated in 3.1).", pointer));
            }
        }

        // Deprecated XML flags become nodeType when crossing the 3.1 → 3.2 boundary.
        if (target == OpenApiSpecVersion.V3_2 && schema.Xml is { NodeType: null } xml)
        {
            ConvertXmlFlagsToNodeType(xml, pointer, diagnostics);
        }
    }

    private static void ApplyDowngrade(OpenApiSchema schema, OpenApiSpecVersion source, OpenApiSpecVersion target, string pointer, List<OpenApiTransformDiagnostic> diagnostics)
    {
        // 3.2 → below: only the attribute/element node types have a deprecated-flag equivalent; the
        // text, cdata, and none node types cannot be represented below 3.2 and are dropped with a warning
        // rather than silently discarded under a misleading "converted" message.
        if (source == OpenApiSpecVersion.V3_2 && schema.Xml is { NodeType: { } nodeType } xml)
        {
            switch (nodeType)
            {
                case XmlNodeType.Attribute:
                    xml.Attribute = true;
                    xml.NodeType = null;
                    diagnostics.Add(Info(OpenApiTransformDiagnosticCodes.XmlNodeTypeConverted, "Converted 'nodeType: attribute' to the deprecated 'attribute' flag.", pointer));
                    break;
                case XmlNodeType.Element:
                    xml.Wrapped = true;
                    xml.NodeType = null;
                    diagnostics.Add(Info(OpenApiTransformDiagnosticCodes.XmlNodeTypeConverted, "Converted 'nodeType: element' to the deprecated 'wrapped' flag.", pointer));
                    break;
                default:
                    xml.NodeType = null;
                    diagnostics.Add(Warning(OpenApiTransformDiagnosticCodes.UnsupportedConstruct, $"XML 'nodeType: {nodeType.ToString().ToLowerInvariant()}' has no representation below OpenAPI 3.2 and was dropped.", pointer));
                    break;
            }
        }

        if (target != OpenApiSpecVersion.V3_0)
        {
            return;
        }

        // Downgrades to 3.0 lose JSON Schema 2020-12 surfaces.
        if (schema.Const is not null && schema.Enum.Count == 0)
        {
            schema.Enum.Add(schema.Const);
            schema.Const = null;
            diagnostics.Add(Info(OpenApiTransformDiagnosticCodes.ConstConverted, "Converted 'const' to a single-value 'enum' (3.0 has no 'const').", pointer));
        }

        if (schema.Examples.Count > 0 && schema.Example is null)
        {
            schema.Example = schema.Examples[0];
            schema.Examples.Clear();
            diagnostics.Add(Info(OpenApiTransformDiagnosticCodes.ExamplesConverted, "Reduced the 'examples' array to the singular 'example' (3.0 has no schema 'examples').", pointer));
        }

        if (schema.Types.Count > 1)
        {
            var kept = schema.Types[0];
            schema.Types.Clear();
            schema.Types.Add(kept);
            diagnostics.Add(Warning(OpenApiTransformDiagnosticCodes.MultiTypeReduced, $"Reduced a multi-type schema to its first type '{kept}' (3.0 allows a single type).", pointer));
        }
    }

    private static void ConvertXmlFlagsToNodeType(OpenApiXml xml, string pointer, List<OpenApiTransformDiagnostic> diagnostics)
    {
        // nodeType is single-valued, so the two deprecated flags cannot both survive. attribute wins;
        // a coincident wrapped flag is dropped with a warning rather than silently lost.
        if (xml.Attribute)
        {
            xml.NodeType = XmlNodeType.Attribute;
            xml.Attribute = false;
            if (xml.Wrapped)
            {
                xml.Wrapped = false;
                diagnostics.Add(Warning(OpenApiTransformDiagnosticCodes.XmlNodeTypeConverted, "Both 'attribute' and 'wrapped' were set; kept 'nodeType: attribute' and dropped 'wrapped'.", pointer));
            }
            else
            {
                diagnostics.Add(Info(OpenApiTransformDiagnosticCodes.XmlNodeTypeConverted, "Converted 'attribute: true' to 'nodeType: attribute'.", pointer));
            }
        }
        else if (xml.Wrapped)
        {
            xml.NodeType = XmlNodeType.Element;
            xml.Wrapped = false;
            diagnostics.Add(Info(OpenApiTransformDiagnosticCodes.XmlNodeTypeConverted, "Converted 'wrapped: true' to 'nodeType: element'.", pointer));
        }
    }

    private static void AnalyzeVersionFit(OpenApiDocument document, OpenApiSpecVersion target, List<OpenApiTransformDiagnostic> diagnostics)
    {
        // Reuse the validator's version-placement rule (the same capability matrix the serializer uses)
        // to surface every construct the target line cannot represent. These are the manual-intervention
        // cases: they are dropped when the transformed document is serialized at the target line.
        foreach (var diagnostic in document.Validate().Diagnostics)
        {
            if (diagnostic.Code == OpenApiValidationRuleCodes.UnsupportedInVersion)
            {
                diagnostics.Add(new OpenApiTransformDiagnostic(
                    OpenApiTransformSeverity.Warning,
                    OpenApiTransformDiagnosticCodes.UnsupportedConstruct,
                    $"{diagnostic.Message} It is dropped when serialized at OpenAPI {OpenApiVersionCapabilities.GetVersionString(target)}.",
                    diagnostic.Location));
            }
        }
    }

    private static OpenApiTransformDiagnostic Info(string code, string message, string pointer) =>
        new(OpenApiTransformSeverity.Information, code, message, pointer);

    private static OpenApiTransformDiagnostic Warning(string code, string message, string pointer) =>
        new(OpenApiTransformSeverity.Warning, code, message, pointer);
}
