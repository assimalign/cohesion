using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.OpenApi.Serialization;

/// <summary>
/// Maps the canonical <see cref="OpenApiDocument"/> graph to a format-agnostic <see cref="OpenApiNode"/>
/// tree for a specific target version. Fields that are not valid for the target version (per
/// <see cref="OpenApiVersionCapabilities"/>) are omitted, and the two normalized version differences —
/// nullability and exclusive numeric bounds — are emitted in the form the target version expects.
/// </summary>
internal static class OpenApiModelToNodeConverter
{
    internal static OpenApiObjectNode Convert(OpenApiDocument document, OpenApiSpecVersion version)
    {
        var root = new OpenApiObjectNode
        {
            ["openapi"] = OpenApiVersionCapabilities.GetVersionString(version)
        };

        if (document.Self is not null && OpenApiVersionCapabilities.Supports(OpenApiFeature.DocumentSelf, version))
        {
            root["$self"] = document.Self;
        }

        root["info"] = ConvertInfo(document.Info, version);

        if (document.JsonSchemaDialect is not null && OpenApiVersionCapabilities.Supports(OpenApiFeature.JsonSchemaDialect, version))
        {
            root["jsonSchemaDialect"] = document.JsonSchemaDialect;
        }

        SetList(root, "servers", document.Servers, s => ConvertServer(s, version));

        if (document.Paths is not null)
        {
            root["paths"] = ConvertPaths(document.Paths, version);
        }

        if (OpenApiVersionCapabilities.Supports(OpenApiFeature.Webhooks, version))
        {
            SetMap(root, "webhooks", document.Webhooks, p => ConvertPathItem(p, version));
        }

        if (document.Components is not null)
        {
            var components = ConvertComponents(document.Components, version);
            if (components.Count > 0)
            {
                root["components"] = components;
            }
        }

        SetList(root, "security", document.Security, ConvertSecurityRequirement);
        SetList(root, "tags", document.Tags, t => ConvertTag(t, version));

        if (document.ExternalDocs is not null)
        {
            root["externalDocs"] = ConvertExternalDocs(document.ExternalDocs);
        }

        SetExtensions(root, document.Extensions);
        return root;
    }

    private static OpenApiObjectNode ConvertInfo(OpenApiInfo info, OpenApiSpecVersion version)
    {
        var node = new OpenApiObjectNode { ["title"] = info.Title };

        if (info.Summary is not null && OpenApiVersionCapabilities.Supports(OpenApiFeature.InfoSummary, version))
        {
            node["summary"] = info.Summary;
        }

        SetString(node, "description", info.Description);
        SetString(node, "termsOfService", info.TermsOfService);

        if (info.Contact is not null)
        {
            node["contact"] = ConvertContact(info.Contact);
        }

        if (info.License is not null)
        {
            node["license"] = ConvertLicense(info.License, version);
        }

        node["version"] = info.Version;
        SetExtensions(node, info.Extensions);
        return node;
    }

    private static OpenApiObjectNode ConvertContact(OpenApiContact contact)
    {
        var node = new OpenApiObjectNode();
        SetString(node, "name", contact.Name);
        SetString(node, "url", contact.Url);
        SetString(node, "email", contact.Email);
        SetExtensions(node, contact.Extensions);
        return node;
    }

    private static OpenApiObjectNode ConvertLicense(OpenApiLicense license, OpenApiSpecVersion version)
    {
        var node = new OpenApiObjectNode { ["name"] = license.Name };

        if (license.Identifier is not null && OpenApiVersionCapabilities.Supports(OpenApiFeature.LicenseIdentifier, version))
        {
            node["identifier"] = license.Identifier;
        }

        SetString(node, "url", license.Url);
        SetExtensions(node, license.Extensions);
        return node;
    }

    private static OpenApiObjectNode ConvertServer(OpenApiServer server, OpenApiSpecVersion version)
    {
        var node = new OpenApiObjectNode { ["url"] = server.Url };
        SetString(node, "description", server.Description);
        SetMap(node, "variables", server.Variables, ConvertServerVariable);
        SetExtensions(node, server.Extensions);
        return node;
    }

    private static OpenApiObjectNode ConvertServerVariable(OpenApiServerVariable variable)
    {
        var node = new OpenApiObjectNode();
        SetList(node, "enum", variable.Enum, v => (OpenApiNode)v);
        node["default"] = variable.Default;
        SetString(node, "description", variable.Description);
        SetExtensions(node, variable.Extensions);
        return node;
    }

    private static OpenApiObjectNode ConvertPaths(OpenApiPaths paths, OpenApiSpecVersion version)
    {
        var node = new OpenApiObjectNode();
        foreach (var key in Sorted(paths.Items.Keys))
        {
            node[key] = ConvertPathItem(paths.Items[key], version);
        }

        SetExtensions(node, paths.Extensions);
        return node;
    }

    private static OpenApiNode ConvertPathItem(OpenApiPathItem item, OpenApiSpecVersion version)
    {
        if (item.Reference is not null)
        {
            return ConvertReference(item.Reference, version);
        }

        var node = new OpenApiObjectNode();
        SetString(node, "summary", item.Summary);
        SetString(node, "description", item.Description);

        foreach (var operation in item.Operations)
        {
            node[OperationTypeString(operation.Key)] = ConvertOperation(operation.Value, version);
        }

        if (item.AdditionalOperations.Count > 0 && OpenApiVersionCapabilities.Supports(OpenApiFeature.PathItemAdditionalOperations, version))
        {
            var additional = new OpenApiObjectNode();
            foreach (var key in Sorted(item.AdditionalOperations.Keys))
            {
                additional[key] = ConvertOperation(item.AdditionalOperations[key], version);
            }

            node["additionalOperations"] = additional;
        }

        SetList(node, "servers", item.Servers, s => ConvertServer(s, version));
        SetList(node, "parameters", item.Parameters, p => ConvertParameter(p, version));
        SetExtensions(node, item.Extensions);
        return node;
    }

    private static OpenApiObjectNode ConvertOperation(OpenApiOperation operation, OpenApiSpecVersion version)
    {
        var node = new OpenApiObjectNode();
        SetList(node, "tags", operation.Tags, t => (OpenApiNode)t);
        SetString(node, "summary", operation.Summary);
        SetString(node, "description", operation.Description);

        if (operation.ExternalDocs is not null)
        {
            node["externalDocs"] = ConvertExternalDocs(operation.ExternalDocs);
        }

        SetString(node, "operationId", operation.OperationId);
        SetList(node, "parameters", operation.Parameters, p => ConvertParameter(p, version));

        if (operation.RequestBody is not null)
        {
            node["requestBody"] = ConvertRequestBody(operation.RequestBody, version);
        }

        if (operation.Responses is not null)
        {
            node["responses"] = ConvertResponses(operation.Responses, version);
        }

        SetMap(node, "callbacks", operation.Callbacks, c => ConvertCallback(c, version));

        if (operation.Deprecated)
        {
            node["deprecated"] = true;
        }

        SetList(node, "security", operation.Security, ConvertSecurityRequirement);
        SetList(node, "servers", operation.Servers, s => ConvertServer(s, version));
        SetExtensions(node, operation.Extensions);
        return node;
    }

    private static OpenApiNode ConvertParameter(OpenApiParameter parameter, OpenApiSpecVersion version)
    {
        if (parameter.Reference is not null)
        {
            return ConvertReference(parameter.Reference, version);
        }

        var node = new OpenApiObjectNode
        {
            ["name"] = parameter.Name,
            ["in"] = ParameterLocationString(parameter.In)
        };

        SetString(node, "description", parameter.Description);

        if (parameter.Required)
        {
            node["required"] = true;
        }

        if (parameter.Deprecated)
        {
            node["deprecated"] = true;
        }

        if (parameter.AllowEmptyValue)
        {
            node["allowEmptyValue"] = true;
        }

        WriteParameterSerialization(node, parameter.Style, parameter.Explode, parameter.AllowReserved);

        if (parameter.Schema is not null)
        {
            node["schema"] = ConvertSchema(parameter.Schema, version);
        }

        if (parameter.Example is not null)
        {
            node["example"] = parameter.Example;
        }

        SetMap(node, "examples", parameter.Examples, e => ConvertExample(e, version));
        SetMap(node, "content", parameter.Content, m => ConvertMediaType(m, version));
        SetExtensions(node, parameter.Extensions);
        return node;
    }

    private static OpenApiNode ConvertHeader(OpenApiHeader header, OpenApiSpecVersion version)
    {
        if (header.Reference is not null)
        {
            return ConvertReference(header.Reference, version);
        }

        var node = new OpenApiObjectNode();
        SetString(node, "description", header.Description);

        if (header.Required)
        {
            node["required"] = true;
        }

        if (header.Deprecated)
        {
            node["deprecated"] = true;
        }

        WriteParameterSerialization(node, header.Style, header.Explode, header.AllowReserved);

        if (header.Schema is not null)
        {
            node["schema"] = ConvertSchema(header.Schema, version);
        }

        if (header.Example is not null)
        {
            node["example"] = header.Example;
        }

        SetMap(node, "examples", header.Examples, e => ConvertExample(e, version));
        SetMap(node, "content", header.Content, m => ConvertMediaType(m, version));
        SetExtensions(node, header.Extensions);
        return node;
    }

    private static void WriteParameterSerialization(OpenApiObjectNode node, ParameterStyle? style, bool? explode, bool allowReserved)
    {
        if (style.HasValue)
        {
            node["style"] = ParameterStyleString(style.Value);
        }

        if (explode.HasValue)
        {
            node["explode"] = explode.Value;
        }

        if (allowReserved)
        {
            node["allowReserved"] = true;
        }
    }

    private static OpenApiNode ConvertRequestBody(OpenApiRequestBody body, OpenApiSpecVersion version)
    {
        if (body.Reference is not null)
        {
            return ConvertReference(body.Reference, version);
        }

        var node = new OpenApiObjectNode();
        SetString(node, "description", body.Description);
        SetMap(node, "content", body.Content, m => ConvertMediaType(m, version));

        if (body.Required)
        {
            node["required"] = true;
        }

        SetExtensions(node, body.Extensions);
        return node;
    }

    private static OpenApiObjectNode ConvertMediaType(OpenApiMediaType media, OpenApiSpecVersion version)
    {
        var node = new OpenApiObjectNode();

        if (media.Schema is not null)
        {
            node["schema"] = ConvertSchema(media.Schema, version);
        }

        if (media.Example is not null)
        {
            node["example"] = media.Example;
        }

        SetMap(node, "examples", media.Examples, e => ConvertExample(e, version));
        SetMap(node, "encoding", media.Encoding, e => ConvertEncoding(e, version));
        SetExtensions(node, media.Extensions);
        return node;
    }

    private static OpenApiObjectNode ConvertEncoding(OpenApiEncoding encoding, OpenApiSpecVersion version)
    {
        var node = new OpenApiObjectNode();
        SetString(node, "contentType", encoding.ContentType);
        SetMap(node, "headers", encoding.Headers, h => ConvertHeader(h, version));
        WriteParameterSerialization(node, encoding.Style, encoding.Explode, encoding.AllowReserved);
        SetExtensions(node, encoding.Extensions);
        return node;
    }

    private static OpenApiObjectNode ConvertResponses(OpenApiResponses responses, OpenApiSpecVersion version)
    {
        var node = new OpenApiObjectNode();
        foreach (var key in Sorted(responses.Items.Keys))
        {
            node[key] = ConvertResponse(responses.Items[key], version);
        }

        SetExtensions(node, responses.Extensions);
        return node;
    }

    private static OpenApiNode ConvertResponse(OpenApiResponse response, OpenApiSpecVersion version)
    {
        if (response.Reference is not null)
        {
            return ConvertReference(response.Reference, version);
        }

        var node = new OpenApiObjectNode { ["description"] = response.Description };
        SetMap(node, "headers", response.Headers, h => ConvertHeader(h, version));
        SetMap(node, "content", response.Content, m => ConvertMediaType(m, version));
        SetMap(node, "links", response.Links, l => ConvertLink(l, version));
        SetExtensions(node, response.Extensions);
        return node;
    }

    private static OpenApiNode ConvertExample(OpenApiExample example, OpenApiSpecVersion version)
    {
        if (example.Reference is not null)
        {
            return ConvertReference(example.Reference, version);
        }

        var node = new OpenApiObjectNode();
        SetString(node, "summary", example.Summary);
        SetString(node, "description", example.Description);

        if (example.Value is not null)
        {
            node["value"] = example.Value;
        }

        SetString(node, "externalValue", example.ExternalValue);
        SetExtensions(node, example.Extensions);
        return node;
    }

    private static OpenApiNode ConvertLink(OpenApiLink link, OpenApiSpecVersion version)
    {
        if (link.Reference is not null)
        {
            return ConvertReference(link.Reference, version);
        }

        var node = new OpenApiObjectNode();
        SetString(node, "operationRef", link.OperationRef);
        SetString(node, "operationId", link.OperationId);
        SetMap(node, "parameters", link.Parameters, n => n);

        if (link.RequestBody is not null)
        {
            node["requestBody"] = link.RequestBody;
        }

        SetString(node, "description", link.Description);

        if (link.Server is not null)
        {
            node["server"] = ConvertServer(link.Server, version);
        }

        SetExtensions(node, link.Extensions);
        return node;
    }

    private static OpenApiNode ConvertCallback(OpenApiCallback callback, OpenApiSpecVersion version)
    {
        if (callback.Reference is not null)
        {
            return ConvertReference(callback.Reference, version);
        }

        var node = new OpenApiObjectNode();
        foreach (var key in Sorted(callback.PathItems.Keys))
        {
            node[key] = ConvertPathItem(callback.PathItems[key], version);
        }

        SetExtensions(node, callback.Extensions);
        return node;
    }

    private static OpenApiObjectNode ConvertComponents(OpenApiComponents components, OpenApiSpecVersion version)
    {
        var node = new OpenApiObjectNode();
        SetMap(node, "schemas", components.Schemas, s => ConvertSchema(s, version));
        SetMap(node, "responses", components.Responses, r => ConvertResponse(r, version));
        SetMap(node, "parameters", components.Parameters, p => ConvertParameter(p, version));
        SetMap(node, "examples", components.Examples, e => ConvertExample(e, version));
        SetMap(node, "requestBodies", components.RequestBodies, r => ConvertRequestBody(r, version));
        SetMap(node, "headers", components.Headers, h => ConvertHeader(h, version));
        SetMap(node, "securitySchemes", components.SecuritySchemes, s => ConvertSecurityScheme(s, version));
        SetMap(node, "links", components.Links, l => ConvertLink(l, version));
        SetMap(node, "callbacks", components.Callbacks, c => ConvertCallback(c, version));

        if (OpenApiVersionCapabilities.Supports(OpenApiFeature.ComponentsPathItems, version))
        {
            SetMap(node, "pathItems", components.PathItems, p => ConvertPathItem(p, version));
        }

        SetExtensions(node, components.Extensions);
        return node;
    }

    private static OpenApiNode ConvertSchema(OpenApiSchema schema, OpenApiSpecVersion version)
    {
        if (schema.Reference is not null)
        {
            return ConvertReference(schema.Reference, version);
        }

        var node = new OpenApiObjectNode();
        SetString(node, "title", schema.Title);
        WriteSchemaType(node, schema, version);
        SetString(node, "format", schema.Format);
        SetString(node, "description", schema.Description);

        if (schema.Default is not null)
        {
            node["default"] = schema.Default;
        }

        SetList(node, "enum", schema.Enum, n => n);

        if (schema.Const is not null && OpenApiVersionCapabilities.Supports(OpenApiFeature.SchemaConst, version))
        {
            node["const"] = schema.Const;
        }

        if (schema.MultipleOf.HasValue)
        {
            node["multipleOf"] = schema.MultipleOf.Value;
        }

        WriteNumberBounds(node, schema, version);
        WriteIntField(node, "maxLength", schema.MaxLength);
        WriteIntField(node, "minLength", schema.MinLength);
        SetString(node, "pattern", schema.Pattern);
        WriteIntField(node, "maxItems", schema.MaxItems);
        WriteIntField(node, "minItems", schema.MinItems);

        if (schema.UniqueItems.HasValue)
        {
            node["uniqueItems"] = schema.UniqueItems.Value;
        }

        WriteIntField(node, "maxProperties", schema.MaxProperties);
        WriteIntField(node, "minProperties", schema.MinProperties);
        SetList(node, "required", schema.Required, v => (OpenApiNode)v);
        SetMap(node, "properties", schema.Properties, s => ConvertSchema(s, version));

        if (schema.AdditionalProperties is not null)
        {
            node["additionalProperties"] = ConvertSchema(schema.AdditionalProperties, version);
        }
        else if (schema.AdditionalPropertiesAllowed.HasValue)
        {
            node["additionalProperties"] = schema.AdditionalPropertiesAllowed.Value;
        }

        if (schema.Items is not null)
        {
            node["items"] = ConvertSchema(schema.Items, version);
        }

        SetList(node, "allOf", schema.AllOf, s => ConvertSchema(s, version));
        SetList(node, "anyOf", schema.AnyOf, s => ConvertSchema(s, version));
        SetList(node, "oneOf", schema.OneOf, s => ConvertSchema(s, version));

        if (schema.Not is not null)
        {
            node["not"] = ConvertSchema(schema.Not, version);
        }

        if (schema.Discriminator is not null)
        {
            node["discriminator"] = ConvertDiscriminator(schema.Discriminator);
        }

        if (schema.ReadOnly)
        {
            node["readOnly"] = true;
        }

        if (schema.WriteOnly)
        {
            node["writeOnly"] = true;
        }

        if (schema.Xml is not null)
        {
            node["xml"] = ConvertXml(schema.Xml);
        }

        if (schema.ExternalDocs is not null)
        {
            node["externalDocs"] = ConvertExternalDocs(schema.ExternalDocs);
        }

        if (schema.Example is not null)
        {
            node["example"] = schema.Example;
        }

        if (schema.Examples.Count > 0 && OpenApiVersionCapabilities.Supports(OpenApiFeature.SchemaExamples, version))
        {
            var arr = new OpenApiArrayNode();
            foreach (var item in schema.Examples)
            {
                arr.Add(item);
            }

            node["examples"] = arr;
        }

        if (schema.Deprecated)
        {
            node["deprecated"] = true;
        }

        SetExtensions(node, schema.Extensions);
        return node;
    }

    private static void WriteSchemaType(OpenApiObjectNode node, OpenApiSchema schema, OpenApiSpecVersion version)
    {
        if (schema.Type is null)
        {
            return;
        }

        var typeName = SchemaTypeString(schema.Type.Value);

        if (version == OpenApiSpecVersion.V3_0)
        {
            node["type"] = typeName;
            if (schema.Nullable)
            {
                node["nullable"] = true;
            }
        }
        else if (schema.Nullable)
        {
            node["type"] = new OpenApiArrayNode { typeName, "null" };
        }
        else
        {
            node["type"] = typeName;
        }
    }

    private static void WriteNumberBounds(OpenApiObjectNode node, OpenApiSchema schema, OpenApiSpecVersion version)
    {
        if (version == OpenApiSpecVersion.V3_0)
        {
            if (schema.ExclusiveMaximum.HasValue)
            {
                node["maximum"] = schema.ExclusiveMaximum.Value;
                node["exclusiveMaximum"] = true;
            }
            else if (schema.Maximum.HasValue)
            {
                node["maximum"] = schema.Maximum.Value;
            }

            if (schema.ExclusiveMinimum.HasValue)
            {
                node["minimum"] = schema.ExclusiveMinimum.Value;
                node["exclusiveMinimum"] = true;
            }
            else if (schema.Minimum.HasValue)
            {
                node["minimum"] = schema.Minimum.Value;
            }
        }
        else
        {
            if (schema.Maximum.HasValue)
            {
                node["maximum"] = schema.Maximum.Value;
            }

            if (schema.ExclusiveMaximum.HasValue)
            {
                node["exclusiveMaximum"] = schema.ExclusiveMaximum.Value;
            }

            if (schema.Minimum.HasValue)
            {
                node["minimum"] = schema.Minimum.Value;
            }

            if (schema.ExclusiveMinimum.HasValue)
            {
                node["exclusiveMinimum"] = schema.ExclusiveMinimum.Value;
            }
        }
    }

    private static OpenApiObjectNode ConvertDiscriminator(OpenApiDiscriminator discriminator)
    {
        var node = new OpenApiObjectNode { ["propertyName"] = discriminator.PropertyName };

        if (discriminator.Mapping.Count > 0)
        {
            var mapping = new OpenApiObjectNode();
            foreach (var key in Sorted(discriminator.Mapping.Keys))
            {
                mapping[key] = discriminator.Mapping[key];
            }

            node["mapping"] = mapping;
        }

        SetExtensions(node, discriminator.Extensions);
        return node;
    }

    private static OpenApiObjectNode ConvertXml(OpenApiXml xml)
    {
        var node = new OpenApiObjectNode();
        SetString(node, "name", xml.Name);
        SetString(node, "namespace", xml.Namespace);
        SetString(node, "prefix", xml.Prefix);

        if (xml.Attribute)
        {
            node["attribute"] = true;
        }

        if (xml.Wrapped)
        {
            node["wrapped"] = true;
        }

        SetExtensions(node, xml.Extensions);
        return node;
    }

    private static OpenApiNode ConvertSecurityScheme(OpenApiSecurityScheme scheme, OpenApiSpecVersion version)
    {
        if (scheme.Reference is not null)
        {
            return ConvertReference(scheme.Reference, version);
        }

        var node = new OpenApiObjectNode { ["type"] = SecuritySchemeTypeString(scheme.Type) };
        SetString(node, "description", scheme.Description);
        SetString(node, "name", scheme.Name);

        if (scheme.In.HasValue)
        {
            node["in"] = ParameterLocationString(scheme.In.Value);
        }

        SetString(node, "scheme", scheme.Scheme);
        SetString(node, "bearerFormat", scheme.BearerFormat);

        if (scheme.Flows is not null)
        {
            node["flows"] = ConvertOAuthFlows(scheme.Flows, version);
        }

        SetString(node, "openIdConnectUrl", scheme.OpenIdConnectUrl);
        SetExtensions(node, scheme.Extensions);
        return node;
    }

    private static OpenApiObjectNode ConvertOAuthFlows(OpenApiOAuthFlows flows, OpenApiSpecVersion version)
    {
        var node = new OpenApiObjectNode();

        if (flows.Implicit is not null)
        {
            node["implicit"] = ConvertOAuthFlow(flows.Implicit);
        }

        if (flows.Password is not null)
        {
            node["password"] = ConvertOAuthFlow(flows.Password);
        }

        if (flows.ClientCredentials is not null)
        {
            node["clientCredentials"] = ConvertOAuthFlow(flows.ClientCredentials);
        }

        if (flows.AuthorizationCode is not null)
        {
            node["authorizationCode"] = ConvertOAuthFlow(flows.AuthorizationCode);
        }

        if (flows.DeviceAuthorization is not null && OpenApiVersionCapabilities.Supports(OpenApiFeature.OAuthDeviceAuthorizationFlow, version))
        {
            node["deviceAuthorization"] = ConvertOAuthFlow(flows.DeviceAuthorization);
        }

        SetExtensions(node, flows.Extensions);
        return node;
    }

    private static OpenApiObjectNode ConvertOAuthFlow(OpenApiOAuthFlow flow)
    {
        var node = new OpenApiObjectNode();
        SetString(node, "authorizationUrl", flow.AuthorizationUrl);
        SetString(node, "tokenUrl", flow.TokenUrl);
        SetString(node, "refreshUrl", flow.RefreshUrl);

        var scopes = new OpenApiObjectNode();
        foreach (var key in Sorted(flow.Scopes.Keys))
        {
            scopes[key] = flow.Scopes[key];
        }

        node["scopes"] = scopes;
        SetExtensions(node, flow.Extensions);
        return node;
    }

    private static OpenApiObjectNode ConvertSecurityRequirement(OpenApiSecurityRequirement requirement)
    {
        var node = new OpenApiObjectNode();
        foreach (var key in Sorted(requirement.Schemes.Keys))
        {
            var scopes = new OpenApiArrayNode();
            foreach (var scope in requirement.Schemes[key])
            {
                scopes.Add(scope);
            }

            node[key] = scopes;
        }

        return node;
    }

    private static OpenApiObjectNode ConvertTag(OpenApiTag tag, OpenApiSpecVersion version)
    {
        var node = new OpenApiObjectNode { ["name"] = tag.Name };

        if (OpenApiVersionCapabilities.Supports(OpenApiFeature.TagExtendedMetadata, version))
        {
            SetString(node, "summary", tag.Summary);
        }

        SetString(node, "description", tag.Description);

        if (OpenApiVersionCapabilities.Supports(OpenApiFeature.TagExtendedMetadata, version))
        {
            SetString(node, "parent", tag.Parent);
            SetString(node, "kind", tag.Kind);
        }

        if (tag.ExternalDocs is not null)
        {
            node["externalDocs"] = ConvertExternalDocs(tag.ExternalDocs);
        }

        SetExtensions(node, tag.Extensions);
        return node;
    }

    private static OpenApiObjectNode ConvertExternalDocs(OpenApiExternalDocumentation docs)
    {
        var node = new OpenApiObjectNode();
        SetString(node, "description", docs.Description);
        node["url"] = docs.Url;
        SetExtensions(node, docs.Extensions);
        return node;
    }

    private static OpenApiObjectNode ConvertReference(OpenApiReference reference, OpenApiSpecVersion version)
    {
        var node = new OpenApiObjectNode { ["$ref"] = reference.Ref };

        if (OpenApiVersionCapabilities.Supports(OpenApiFeature.ReferenceSummaryAndDescription, version))
        {
            SetString(node, "summary", reference.Summary);
            SetString(node, "description", reference.Description);
        }

        return node;
    }

    private static void SetString(OpenApiObjectNode node, string key, string? value)
    {
        if (value is not null)
        {
            node[key] = value;
        }
    }

    private static void WriteIntField(OpenApiObjectNode node, string key, int? value)
    {
        if (value.HasValue)
        {
            node[key] = value.Value;
        }
    }

    private static void SetList<T>(OpenApiObjectNode node, string key, IList<T> items, Func<T, OpenApiNode> map)
    {
        if (items.Count == 0)
        {
            return;
        }

        var array = new OpenApiArrayNode();
        foreach (var item in items)
        {
            array.Add(map(item));
        }

        node[key] = array;
    }

    private static void SetMap<T>(OpenApiObjectNode node, string key, IDictionary<string, T> items, Func<T, OpenApiNode> map)
    {
        if (items.Count == 0)
        {
            return;
        }

        var obj = new OpenApiObjectNode();
        foreach (var itemKey in Sorted(items.Keys))
        {
            obj[itemKey] = map(items[itemKey]);
        }

        node[key] = obj;
    }

    private static void SetExtensions(OpenApiObjectNode node, IDictionary<string, OpenApiNode> extensions)
    {
        if (extensions.Count == 0)
        {
            return;
        }

        foreach (var key in Sorted(extensions.Keys))
        {
            node[key] = extensions[key];
        }
    }

    private static List<string> Sorted(IEnumerable<string> keys)
    {
        var list = new List<string>(keys);
        list.Sort(StringComparer.Ordinal);
        return list;
    }

    private static string OperationTypeString(OperationType type) => type switch
    {
        OperationType.Get => "get",
        OperationType.Put => "put",
        OperationType.Post => "post",
        OperationType.Delete => "delete",
        OperationType.Options => "options",
        OperationType.Head => "head",
        OperationType.Patch => "patch",
        OperationType.Trace => "trace",
        _ => throw new OpenApiException($"Unknown operation type '{type}'.")
    };

    private static string ParameterLocationString(ParameterLocation location) => location switch
    {
        ParameterLocation.Query => "query",
        ParameterLocation.Header => "header",
        ParameterLocation.Path => "path",
        ParameterLocation.Cookie => "cookie",
        _ => throw new OpenApiException($"Unknown parameter location '{location}'.")
    };

    private static string ParameterStyleString(ParameterStyle style) => style switch
    {
        ParameterStyle.Matrix => "matrix",
        ParameterStyle.Label => "label",
        ParameterStyle.Simple => "simple",
        ParameterStyle.Form => "form",
        ParameterStyle.SpaceDelimited => "spaceDelimited",
        ParameterStyle.PipeDelimited => "pipeDelimited",
        ParameterStyle.DeepObject => "deepObject",
        _ => throw new OpenApiException($"Unknown parameter style '{style}'.")
    };

    private static string SchemaTypeString(SchemaType type) => type switch
    {
        SchemaType.Boolean => "boolean",
        SchemaType.Object => "object",
        SchemaType.Array => "array",
        SchemaType.Number => "number",
        SchemaType.String => "string",
        SchemaType.Integer => "integer",
        SchemaType.Null => "null",
        _ => throw new OpenApiException($"Unknown schema type '{type}'.")
    };

    private static string SecuritySchemeTypeString(SecuritySchemeType type) => type switch
    {
        SecuritySchemeType.ApiKey => "apiKey",
        SecuritySchemeType.Http => "http",
        SecuritySchemeType.MutualTLS => "mutualTLS",
        SecuritySchemeType.OAuth2 => "oauth2",
        SecuritySchemeType.OpenIdConnect => "openIdConnect",
        _ => throw new OpenApiException($"Unknown security scheme type '{type}'.")
    };
}
