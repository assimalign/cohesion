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
        ValidateQuerystringUsage(context, document);
        ValidateResponseKeys(context, document);
        ValidateSecurity(context, document);
        ValidateExamples(context, document);
        ValidateLinks(context, document);
        ValidateOAuthFlows(context, document);
        ValidateAdditionalOperations(context, document);
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
        var seen = new HashSet<(string Name, ParameterLocation In)>();
        for (var index = 0; index < parameters.Count; index++)
        {
            var parameter = parameters[index];
            ValidateParameter(context, parameter, JsonPointer.Append(pointer, index.ToString()));

            if (parameter.Reference is null && !seen.Add((parameter.Name, parameter.In)))
            {
                context.Error(
                    OpenApiValidationRuleCodes.DuplicateParameter,
                    $"Parameter '{parameter.Name}' in '{parameter.In}' is declared more than once in the same list.",
                    JsonPointer.Append(pointer, index.ToString()));
            }
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

        // A querystring parameter describes the entire query string and must use 'content'.
        if (parameter.In == ParameterLocation.Querystring && (parameter.Schema is not null || parameter.Style is not null))
        {
            context.Error(
                OpenApiValidationRuleCodes.QuerystringParameterUsage,
                $"Querystring parameter '{parameter.Name}' must use 'content' and must not declare 'schema' or 'style'.",
                pointer);
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

    private static void ValidateQuerystringUsage(OpenApiValidationContext context, OpenApiDocument document)
    {
        foreach (var entry in OpenApiOperationWalker.Enumerate(document))
        {
            var querystringCount = 0;
            var queryCount = 0;

            void Count(IList<OpenApiParameter> parameters)
            {
                foreach (var parameter in parameters)
                {
                    if (parameter.Reference is not null)
                    {
                        continue;
                    }

                    if (parameter.In == ParameterLocation.Querystring)
                    {
                        querystringCount++;
                    }
                    else if (parameter.In == ParameterLocation.Query)
                    {
                        queryCount++;
                    }
                }
            }

            Count(entry.Operation.Parameters);
            if (entry.IsPath && document.Paths is not null && document.Paths.Items.TryGetValue(entry.PathKey, out var pathItem))
            {
                Count(pathItem.Parameters);
            }

            if (querystringCount > 1)
            {
                context.Error(
                    OpenApiValidationRuleCodes.QuerystringParameterUsage,
                    "An operation must not declare more than one querystring parameter.",
                    JsonPointer.Append(entry.Pointer, "parameters"));
            }

            if (querystringCount > 0 && queryCount > 0)
            {
                context.Error(
                    OpenApiValidationRuleCodes.QuerystringParameterUsage,
                    "A querystring parameter must not coexist with query parameters on the same operation.",
                    JsonPointer.Append(entry.Pointer, "parameters"));
            }
        }
    }

    private static void ValidateExamples(OpenApiValidationContext context, OpenApiDocument document)
    {
        if (document.Components is not null)
        {
            foreach (var example in document.Components.Examples)
            {
                ValidateExample(context, example.Value, JsonPointer.Of("components", "examples", example.Key));
            }
        }

        foreach (var entry in OpenApiOperationWalker.Enumerate(document))
        {
            for (var index = 0; index < entry.Operation.Parameters.Count; index++)
            {
                foreach (var example in entry.Operation.Parameters[index].Examples)
                {
                    ValidateExample(context, example.Value, JsonPointer.Append(entry.Pointer, "parameters", index.ToString(), "examples", example.Key));
                }
            }

            if (entry.Operation.Responses is not null)
            {
                foreach (var response in entry.Operation.Responses.Items)
                {
                    foreach (var media in response.Value.Content)
                    {
                        foreach (var example in media.Value.Examples)
                        {
                            ValidateExample(context, example.Value, JsonPointer.Append(entry.Pointer, "responses", response.Key, "content", media.Key, "examples", example.Key));
                        }
                    }
                }
            }
        }
    }

    private static void ValidateExample(OpenApiValidationContext context, OpenApiExample example, string pointer)
    {
        if (example.Reference is not null)
        {
            return;
        }

        // The 3.2 exclusivity matrix: value excludes every other field; serializedValue excludes externalValue.
        var conflict = (example.Value is not null && (example.ExternalValue is not null || example.DataValue is not null || example.SerializedValue is not null))
            || (example.SerializedValue is not null && example.ExternalValue is not null);

        if (conflict)
        {
            context.Error(
                OpenApiValidationRuleCodes.ExampleValueConflict,
                "The Example Object combines mutually exclusive value fields ('value', 'externalValue', 'dataValue', 'serializedValue').",
                pointer);
        }
    }

    private static void ValidateLinks(OpenApiValidationContext context, OpenApiDocument document)
    {
        if (document.Components is not null)
        {
            foreach (var link in document.Components.Links)
            {
                ValidateLink(context, link.Value, JsonPointer.Of("components", "links", link.Key));
            }
        }

        foreach (var entry in OpenApiOperationWalker.Enumerate(document))
        {
            if (entry.Operation.Responses is null)
            {
                continue;
            }

            foreach (var response in entry.Operation.Responses.Items)
            {
                foreach (var link in response.Value.Links)
                {
                    ValidateLink(context, link.Value, JsonPointer.Append(entry.Pointer, "responses", response.Key, "links", link.Key));
                }
            }
        }
    }

    private static void ValidateLink(OpenApiValidationContext context, OpenApiLink link, string pointer)
    {
        if (link.Reference is null && link.OperationId is not null && link.OperationRef is not null)
        {
            context.Error(
                OpenApiValidationRuleCodes.LinkOperationConflict,
                "The Link Object declares both 'operationId' and 'operationRef', which are mutually exclusive.",
                pointer);
        }
    }

    private static void ValidateOAuthFlows(OpenApiValidationContext context, OpenApiDocument document)
    {
        if (document.Components is null)
        {
            return;
        }

        foreach (var scheme in document.Components.SecuritySchemes)
        {
            var flows = scheme.Value.Flows;
            if (flows is null)
            {
                continue;
            }

            var pointer = JsonPointer.Of("components", "securitySchemes", scheme.Key, "flows");
            CheckFlow(context, flows.Implicit, "implicit", requiresAuthorization: true, requiresToken: false, requiresDevice: false, pointer);
            CheckFlow(context, flows.Password, "password", requiresAuthorization: false, requiresToken: true, requiresDevice: false, pointer);
            CheckFlow(context, flows.ClientCredentials, "clientCredentials", requiresAuthorization: false, requiresToken: true, requiresDevice: false, pointer);
            CheckFlow(context, flows.AuthorizationCode, "authorizationCode", requiresAuthorization: true, requiresToken: true, requiresDevice: false, pointer);
            CheckFlow(context, flows.DeviceAuthorization, "deviceAuthorization", requiresAuthorization: false, requiresToken: true, requiresDevice: true, pointer);
        }
    }

    private static void CheckFlow(OpenApiValidationContext context, OpenApiOAuthFlow? flow, string name, bool requiresAuthorization, bool requiresToken, bool requiresDevice, string pointer)
    {
        if (flow is null)
        {
            return;
        }

        if (requiresAuthorization && string.IsNullOrEmpty(flow.AuthorizationUrl))
        {
            context.Error(OpenApiValidationRuleCodes.IncompleteOAuthFlow, $"The '{name}' OAuth flow requires 'authorizationUrl'.", JsonPointer.Append(pointer, name, "authorizationUrl"));
        }

        if (requiresToken && string.IsNullOrEmpty(flow.TokenUrl))
        {
            context.Error(OpenApiValidationRuleCodes.IncompleteOAuthFlow, $"The '{name}' OAuth flow requires 'tokenUrl'.", JsonPointer.Append(pointer, name, "tokenUrl"));
        }

        if (requiresDevice && string.IsNullOrEmpty(flow.DeviceAuthorizationUrl))
        {
            context.Error(OpenApiValidationRuleCodes.IncompleteOAuthFlow, $"The '{name}' OAuth flow requires 'deviceAuthorizationUrl'.", JsonPointer.Append(pointer, name, "deviceAuthorizationUrl"));
        }
    }

    private static void ValidateAdditionalOperations(OpenApiValidationContext context, OpenApiDocument document)
    {
        if (document.Paths is null)
        {
            return;
        }

        foreach (var path in document.Paths.Items)
        {
            foreach (var method in path.Value.AdditionalOperations.Keys)
            {
                if (IsFixedMethod(method))
                {
                    context.Error(
                        OpenApiValidationRuleCodes.ReservedAdditionalOperation,
                        $"The method '{method}' has a fixed operation field and must not appear in 'additionalOperations'.",
                        JsonPointer.Of("paths", path.Key, "additionalOperations", method));
                }
            }
        }
    }

    private static bool IsFixedMethod(string method) =>
        method.Equals("GET", StringComparison.OrdinalIgnoreCase)
        || method.Equals("PUT", StringComparison.OrdinalIgnoreCase)
        || method.Equals("POST", StringComparison.OrdinalIgnoreCase)
        || method.Equals("DELETE", StringComparison.OrdinalIgnoreCase)
        || method.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase)
        || method.Equals("HEAD", StringComparison.OrdinalIgnoreCase)
        || method.Equals("PATCH", StringComparison.OrdinalIgnoreCase)
        || method.Equals("TRACE", StringComparison.OrdinalIgnoreCase)
        || method.Equals("QUERY", StringComparison.OrdinalIgnoreCase);

    private static void ValidatePathParameters(OpenApiValidationContext context, OpenApiDocument document)
    {
        if (document.Paths is null)
        {
            return;
        }

        foreach (var path in document.Paths.Items)
        {
            var placeholders = ExtractPlaceholders(path.Key);

            // A reference we cannot resolve internally may itself declare a path parameter, so it must
            // suppress the missing-parameter check to avoid false positives on external references.
            var hasUnresolvable = HasUnresolvablePathParameter(document, path.Value.Parameters);
            var itemNames = CollectPathParameterNames(document, path.Value.Parameters);
            ReportUndeclared(context, document, path.Value.Parameters, placeholders, JsonPointer.Of("paths", path.Key, "parameters"));

            foreach (var operation in path.Value.Operations)
            {
                var operationPointer = JsonPointer.Of("paths", path.Key, OpenApiOperationWalker.OperationTypeString(operation.Key));
                var operationNames = CollectPathParameterNames(document, operation.Value.Parameters);
                var operationUnresolvable = hasUnresolvable || HasUnresolvablePathParameter(document, operation.Value.Parameters);
                ReportUndeclared(context, document, operation.Value.Parameters, placeholders, JsonPointer.Append(operationPointer, "parameters"));

                if (operationUnresolvable)
                {
                    continue;
                }

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

    private static void ReportUndeclared(OpenApiValidationContext context, OpenApiDocument document, IList<OpenApiParameter> parameters, HashSet<string> placeholders, string pointer)
    {
        for (var index = 0; index < parameters.Count; index++)
        {
            var resolved = Resolve(document, parameters[index]);
            if (resolved is not null && resolved.In == ParameterLocation.Path && !placeholders.Contains(resolved.Name))
            {
                context.Error(
                    OpenApiValidationRuleCodes.UndeclaredPathParameter,
                    $"Path parameter '{resolved.Name}' does not appear in the path template.",
                    JsonPointer.Append(pointer, index.ToString()));
            }
        }
    }

    private static HashSet<string> CollectPathParameterNames(OpenApiDocument document, IList<OpenApiParameter> parameters)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var parameter in parameters)
        {
            var resolved = Resolve(document, parameter);
            if (resolved is not null && resolved.In == ParameterLocation.Path)
            {
                names.Add(resolved.Name);
            }
        }

        return names;
    }

    private static bool HasUnresolvablePathParameter(OpenApiDocument document, IList<OpenApiParameter> parameters)
    {
        foreach (var parameter in parameters)
        {
            if (parameter.Reference is not null && Resolve(document, parameter) is null)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Resolves a parameter to its effective definition, following a single internal
    /// <c>#/components/parameters/{name}</c> reference. Returns <see langword="null"/> when the
    /// parameter is an external or unresolvable reference.
    /// </summary>
    private static OpenApiParameter? Resolve(OpenApiDocument document, OpenApiParameter parameter)
    {
        if (parameter.Reference is null)
        {
            return parameter;
        }

        const string prefix = "#/components/parameters/";
        var reference = parameter.Reference.Ref;
        if (document.Components is not null && reference.StartsWith(prefix, StringComparison.Ordinal))
        {
            var name = reference[prefix.Length..].Replace("~1", "/").Replace("~0", "~");
            if (document.Components.Parameters.TryGetValue(name, out var target) && target.Reference is null)
            {
                return target;
            }
        }

        return null;
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
        // From 3.2 a requirement name may be the URI of a Security Scheme Object rather than a
        // component name; URI-shaped names cannot be resolved against components here.
        var allowUris = OpenApiVersionCapabilities.Supports(OpenApiFeature.SecurityRequirementUriReference, context.Document.SpecVersion);

        foreach (var scheme in requirement.Schemes.Keys)
        {
            if (allowUris && (scheme.Contains(':') || scheme.Contains('/') || scheme.StartsWith('#')))
            {
                continue;
            }

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
