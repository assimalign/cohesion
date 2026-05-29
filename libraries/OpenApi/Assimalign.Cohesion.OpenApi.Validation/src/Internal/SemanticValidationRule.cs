using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.OpenApi.Validation;

/// <summary>
/// Validates cross-field semantics that structural and schema checks do not capture: operation identifier
/// uniqueness, path parameter consistency, parameter shape, response keys, and security scheme references.
/// </summary>
internal sealed class SemanticValidationRule : IOpenApiValidationRule
{
    public void Validate(OpenApiValidationContext context)
    {
        var document = context.Document;

        ValidateOperationIds(context, document);
        ValidateParameters(context, document);
        ValidatePathParameters(context, document);
        ValidateResponseKeys(context, document);
        ValidateSecurity(context, document);
    }

    private static void ValidateOperationIds(OpenApiValidationContext context, OpenApiDocument document)
    {
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in OpenApiOperationWalker.Enumerate(document))
        {
            var operationId = entry.Operation.OperationId;
            if (string.IsNullOrEmpty(operationId))
            {
                continue;
            }

            if (seen.TryGetValue(operationId, out var existing))
            {
                context.Error(
                    OpenApiValidationRuleCodes.DuplicateOperationId,
                    $"Duplicate operationId '{operationId}' is also used at '{existing}'.",
                    JsonPointer.Append(entry.Pointer, "operationId"));
            }
            else
            {
                seen[operationId] = entry.Pointer;
            }
        }
    }

    private static void ValidateParameters(OpenApiValidationContext context, OpenApiDocument document)
    {
        if (document.Paths is not null)
        {
            foreach (var path in document.Paths.Items)
            {
                ValidateParameterList(context, path.Value.Parameters, JsonPointer.Of("paths", path.Key, "parameters"));
            }
        }

        foreach (var entry in OpenApiOperationWalker.Enumerate(document))
        {
            ValidateParameterList(context, entry.Operation.Parameters, JsonPointer.Append(entry.Pointer, "parameters"));
        }

        if (document.Components is not null)
        {
            foreach (var parameter in document.Components.Parameters)
            {
                ValidateParameter(context, parameter.Value, JsonPointer.Of("components", "parameters", parameter.Key));
            }
        }
    }

    private static void ValidateParameterList(OpenApiValidationContext context, IList<OpenApiParameter> parameters, string pointer)
    {
        for (var index = 0; index < parameters.Count; index++)
        {
            ValidateParameter(context, parameters[index], JsonPointer.Append(pointer, index.ToString()));
        }
    }

    private static void ValidateParameter(OpenApiValidationContext context, OpenApiParameter parameter, string pointer)
    {
        if (parameter.Reference is not null)
        {
            return;
        }

        if (parameter.In == ParameterLocation.Path && !parameter.Required)
        {
            context.Error(
                OpenApiValidationRuleCodes.PathParameterNotRequired,
                $"Path parameter '{parameter.Name}' must set 'required' to true.",
                JsonPointer.Append(pointer, "required"));
        }

        var hasSchema = parameter.Schema is not null;
        var hasContent = parameter.Content.Count > 0;
        if (hasSchema == hasContent)
        {
            context.Error(
                OpenApiValidationRuleCodes.ParameterSchemaAndContent,
                $"Parameter '{parameter.Name}' must declare exactly one of 'schema' or 'content'.",
                pointer);
        }
    }

    private static void ValidatePathParameters(OpenApiValidationContext context, OpenApiDocument document)
    {
        if (document.Paths is null)
        {
            return;
        }

        foreach (var path in document.Paths.Items)
        {
            var placeholders = ExtractPlaceholders(path.Key);
            var itemNames = CollectPathParameterNames(path.Value.Parameters);
            ReportUndeclared(context, path.Value.Parameters, placeholders, JsonPointer.Of("paths", path.Key, "parameters"));

            foreach (var operation in path.Value.Operations)
            {
                var operationPointer = JsonPointer.Of("paths", path.Key, OpenApiOperationWalker.OperationTypeString(operation.Key));
                var operationNames = CollectPathParameterNames(operation.Value.Parameters);
                ReportUndeclared(context, operation.Value.Parameters, placeholders, JsonPointer.Append(operationPointer, "parameters"));

                foreach (var placeholder in placeholders)
                {
                    if (!itemNames.Contains(placeholder) && !operationNames.Contains(placeholder))
                    {
                        context.Error(
                            OpenApiValidationRuleCodes.MissingPathParameter,
                            $"Path template '{path.Key}' declares '{{{placeholder}}}' but no matching path parameter is defined.",
                            operationPointer);
                    }
                }
            }
        }
    }

    private static void ReportUndeclared(OpenApiValidationContext context, IList<OpenApiParameter> parameters, HashSet<string> placeholders, string pointer)
    {
        for (var index = 0; index < parameters.Count; index++)
        {
            var parameter = parameters[index];
            if (parameter.Reference is null && parameter.In == ParameterLocation.Path && !placeholders.Contains(parameter.Name))
            {
                context.Error(
                    OpenApiValidationRuleCodes.UndeclaredPathParameter,
                    $"Path parameter '{parameter.Name}' does not appear in the path template.",
                    JsonPointer.Append(pointer, index.ToString()));
            }
        }
    }

    private static HashSet<string> CollectPathParameterNames(IList<OpenApiParameter> parameters)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var parameter in parameters)
        {
            if (parameter.Reference is null && parameter.In == ParameterLocation.Path)
            {
                names.Add(parameter.Name);
            }
        }

        return names;
    }

    private static void ValidateResponseKeys(OpenApiValidationContext context, OpenApiDocument document)
    {
        foreach (var entry in OpenApiOperationWalker.Enumerate(document))
        {
            var responses = entry.Operation.Responses;
            if (responses is null)
            {
                continue;
            }

            foreach (var key in responses.Items.Keys)
            {
                if (!IsValidResponseKey(key))
                {
                    context.Error(
                        OpenApiValidationRuleCodes.InvalidResponseKey,
                        $"Response key '{key}' is not a valid HTTP status code, status-code range, or 'default'.",
                        JsonPointer.Append(entry.Pointer, "responses", key));
                }
            }
        }
    }

    private static void ValidateSecurity(OpenApiValidationContext context, OpenApiDocument document)
    {
        var defined = new HashSet<string>(StringComparer.Ordinal);
        if (document.Components is not null)
        {
            foreach (var scheme in document.Components.SecuritySchemes.Keys)
            {
                defined.Add(scheme);
            }
        }

        for (var index = 0; index < document.Security.Count; index++)
        {
            ValidateSecurityRequirement(context, document.Security[index], defined, JsonPointer.Of("security", index.ToString()));
        }

        foreach (var entry in OpenApiOperationWalker.Enumerate(document))
        {
            for (var index = 0; index < entry.Operation.Security.Count; index++)
            {
                ValidateSecurityRequirement(context, entry.Operation.Security[index], defined, JsonPointer.Append(entry.Pointer, "security", index.ToString()));
            }
        }
    }

    private static void ValidateSecurityRequirement(OpenApiValidationContext context, OpenApiSecurityRequirement requirement, HashSet<string> defined, string pointer)
    {
        foreach (var scheme in requirement.Schemes.Keys)
        {
            if (!defined.Contains(scheme))
            {
                context.Error(
                    OpenApiValidationRuleCodes.UnknownSecurityScheme,
                    $"Security requirement references undefined security scheme '{scheme}'.",
                    JsonPointer.Append(pointer, scheme));
            }
        }
    }

    private static HashSet<string> ExtractPlaceholders(string path)
    {
        var placeholders = new HashSet<string>(StringComparer.Ordinal);
        var start = -1;
        for (var index = 0; index < path.Length; index++)
        {
            if (path[index] == '{')
            {
                start = index + 1;
            }
            else if (path[index] == '}' && start >= 0)
            {
                placeholders.Add(path.Substring(start, index - start));
                start = -1;
            }
        }

        return placeholders;
    }

    private static bool IsValidResponseKey(string key)
    {
        if (key == "default")
        {
            return true;
        }

        if (key.Length != 3 || key[0] < '1' || key[0] > '5')
        {
            return false;
        }

        if (key[1] == 'X' && key[2] == 'X')
        {
            return true;
        }

        return char.IsDigit(key[1]) && char.IsDigit(key[2]);
    }
}
